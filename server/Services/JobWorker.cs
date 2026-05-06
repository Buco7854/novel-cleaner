using System.Text.Json;
using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

public sealed class JobWorker(
    JobQueue queue,
    IServiceScopeFactory scopes,
    IOptions<StorageOptions> storage,
    ILogger<JobWorker> log) : BackgroundService
{
    private readonly StorageOptions _storage = storage.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_storage.UploadDirectory);
        Directory.CreateDirectory(_storage.OutputDirectory);

        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunOneAsync(id, stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Job {JobId} crashed", id);
            }
        }
    }

    private async Task RunOneAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var logger = sp.GetRequiredService<JobLogger>();
        var openai = sp.GetRequiredService<OpenAiClient>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var auth = sp.GetRequiredService<IOptionsMonitor<AuthOptions>>();

        var job = await db.CleanJobs.Include(j => j.User)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await logger.UpdateStatusAsync(job.Id, JobStatus.Running, 0, ct: ct);

        try
        {
            var appSettings = await db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == AppSettings.SingletonKey, ct)
                ?? throw new InvalidOperationException(
                    "Global settings not configured. An admin must configure them in the Settings page.");
            if (string.IsNullOrWhiteSpace(appSettings.ApiKey))
                throw new InvalidOperationException("API key not configured. An admin must set it in the Settings page.");
            if (string.IsNullOrWhiteSpace(job.Model))
                throw new InvalidOperationException("Model not set");

            await logger.LogAsync(jobId, "info",
                $"Loading EPUB: {job.OriginalFileName} ({job.FileSizeBytes:N0} bytes)", ct);

            var docs = EpubHandler.ReadHtmlDocuments(job.InputStoragePath);
            await logger.LogAsync(jobId, "info", $"Found {docs.Count} HTML document(s)", ct);

            var patterns = JsonSerializer.Deserialize<List<string>>(job.PatternsJson) ?? [];

            var queueWork = new List<(EpubDocument Doc, IReadOnlyList<string> Matched, IReadOnlyList<string> Excerpts)>();

            if (job.ScanAll)
            {
                await logger.LogAsync(jobId, "info", "Mode: full-file scan", ct);
                foreach (var d in docs)
                {
                    var text = EpubHandler.ExtractText(d.Content);
                    if (!string.IsNullOrWhiteSpace(text))
                        queueWork.Add((d, [], [text]));
                }
            }
            else
            {
                await logger.LogAsync(jobId, "info",
                    $"Mode: pattern scan · {patterns.Count} pattern(s) · ±{job.ContextWindow} chars", ct);
                foreach (var d in docs)
                {
                    var text = EpubHandler.ExtractText(d.Content);
                    var hit = EpubScanner.Scan(text, patterns, job.ContextWindow);
                    if (hit.MatchedPatterns.Count > 0)
                        queueWork.Add((d, hit.MatchedPatterns, hit.Excerpts));
                }
            }

            if (queueWork.Count == 0)
            {
                await logger.LogAsync(jobId, "clean", "No matches — copying file unchanged.", ct);
                var unchanged = OutputPath(job);
                File.Copy(job.InputStoragePath, unchanged, overwrite: true);
                job.OutputStoragePath = unchanged;
                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await TryCopyToDropFolderAsync(job, appSettings, unchanged, logger, users, auth.CurrentValue, ct);
                await logger.UpdateStatusAsync(jobId, JobStatus.Completed, 100, 0, 0, ct);
                return;
            }

            await logger.LogAsync(jobId, "info",
                $"Sending {queueWork.Count} document(s) to {job.Model}", ct);
            await logger.UpdateStatusAsync(jobId, JobStatus.Running, 0, 0, queueWork.Count, ct);

            var updates = new Dictionary<string, byte[]>();
            var totalRemoved = 0;
            var completed = 0;
            var totalCount = queueWork.Count;

            using var sem = new SemaphoreSlim(Math.Max(1, job.MaxWorkers));

            var tasks = queueWork.Select(async work =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var result = await openai.IdentifyWatermarksAsync(
                        work.Excerpts, work.Matched, appSettings.ApiKey!, appSettings.BaseUrl, job.Model, ct,
                        appSettings.SystemPrompt);

                    if (result.HasWatermarks)
                    {
                        var (newBytes, applied) = EpubHandler.ApplyRemovals(work.Doc.Content, result.Items);
                        if (applied.Count > 0)
                        {
                            lock (updates) updates[work.Doc.Name] = newBytes;
                            Interlocked.Add(ref totalRemoved, applied.Count);
                            await logger.LogAsync(jobId, "removed",
                                $"{work.Doc.Name}: removed {applied.Count} item(s) — {string.Join(" · ", applied.Select(a => Truncate(a.Removed, 80)))}",
                                ct);
                        }
                        else
                        {
                            await logger.LogAsync(jobId, "warn",
                                $"{work.Doc.Name}: LLM flagged but no text matched verbatim", ct);
                        }
                    }
                    else
                    {
                        await logger.LogAsync(jobId, "clean", $"{work.Doc.Name}: clean", ct);
                    }
                }
                catch (Exception ex)
                {
                    await logger.LogAsync(jobId, "error", $"{work.Doc.Name}: {ex.Message}", ct);
                }
                finally
                {
                    sem.Release();
                    var done = Interlocked.Increment(ref completed);
                    var pct = (int)(done * 90.0 / totalCount);
                    await logger.UpdateStatusAsync(jobId, JobStatus.Running, pct, done, totalCount, ct);
                }
            });
            await Task.WhenAll(tasks);

            var outPath = OutputPath(job);
            EpubHandler.WriteUpdatedEpub(job.InputStoragePath, outPath, updates);
            job.OutputStoragePath = outPath;
            job.RemovedCount = totalRemoved;
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await logger.LogAsync(jobId, "summary",
                $"Done — removed {totalRemoved} watermark item(s).", ct);
            await TryCopyToDropFolderAsync(job, appSettings, outPath, logger, users, auth.CurrentValue, ct);
            await logger.UpdateStatusAsync(jobId, JobStatus.Completed, 100, totalCount, totalCount, ct);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Canceled;
            await db.SaveChangesAsync(CancellationToken.None);
            await logger.UpdateStatusAsync(jobId, JobStatus.Canceled);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job {JobId} failed", jobId);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await logger.LogAsync(jobId, "error", ex.Message, CancellationToken.None);
            await logger.UpdateStatusAsync(jobId, JobStatus.Failed);
        }
    }

    private string OutputPath(CleanJob job)
    {
        var ext = Path.GetExtension(job.OriginalFileName);
        var stem = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        return Path.Combine(_storage.OutputDirectory, $"{stem}_{job.Id:N}{(ext.Length > 0 ? ext : ".epub")}");
    }

    private static async Task TryCopyToDropFolderAsync(
        CleanJob job,
        AppSettings settings,
        string sourcePath,
        JobLogger logger,
        UserManager<AppUser> users,
        AuthOptions auth,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.DropFolder)) return;

        // Skip the auto-copy if the job's owner doesn't have the BookDrop
        // permission. We log it (debug-info, not warn) so the operator can
        // see it was intentional.
        if (job.User is null
            || !await Permissions.CanUseDropFolderAsync(users, job.User, auth))
        {
            await logger.LogAsync(job.Id, "info",
                "Drop folder skipped: the job's owner does not have the BookDrop permission.", ct);
            return;
        }

        try
        {
            var result = DropFolderHelper.Copy(settings.DropFolder, job.OriginalFileName, sourcePath);
            await logger.LogAsync(job.Id, "info", $"Copied to drop folder: {result.DestinationPath}", ct);
        }
        catch (Exception ex)
        {
            await logger.LogAsync(job.Id, "warn", $"Drop folder copy failed: {ex.Message}", ct);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

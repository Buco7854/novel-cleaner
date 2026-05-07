using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Applies a review-mode job's accepted proposals (LLM + user) to the
/// original EPUB, writes the cleaned output, marks the job
/// <see cref="JobStatus.Completed"/>, and forwards the file to the drop
/// folder when applicable. Mirrors the post-LLM tail of <see cref="JobWorker"/>
/// so a finalize-from-review produces the same artifact a non-review job
/// would.
/// </summary>
public sealed class JobFinalizer(
    AppDbContext db,
    JobLogger logger,
    UserManager<AppUser> users,
    IOptionsMonitor<AuthOptions> auth,
    IOptions<StorageOptions> storage)
{
    private readonly StorageOptions _storage = storage.Value;

    public async Task<bool> FinalizeAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.CleanJobs
            .Include(j => j.User)
            .Include(j => j.Reviews)
                .ThenInclude(r => r.Proposals)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return false;
        if (job.Status != JobStatus.AwaitingReview) return false;

        var appSettings = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == AppSettings.SingletonKey, ct);

        // Re-read original chapter bytes — we never persisted the cleaned
        // bytes during the LLM pass, by design.
        var docs = EpubHandler.ReadHtmlDocuments(job.InputStoragePath)
            .ToDictionary(d => d.Name, d => d.Content);

        var updates = new Dictionary<string, byte[]>();
        var totalApplied = 0;

        foreach (var review in job.Reviews)
        {
            var accepted = review.Proposals
                .Where(p => p.Decision == ProposalDecision.Accepted)
                .Select(p => new RemovalInstruction(p.Text, p.Reason))
                .ToList();
            if (accepted.Count == 0) continue;
            if (!docs.TryGetValue(review.DocumentName, out var content))
            {
                await logger.LogAsync(jobId, "warn",
                    $"{review.DocumentName}: missing from EPUB at finalize time — skipping",
                    ct, groupId: review.DocumentName);
                continue;
            }
            var (bytes, applied) = EpubHandler.ApplyRemovals(content, accepted);
            if (applied.Count == 0) continue;
            updates[review.DocumentName] = bytes;
            totalApplied += applied.Count;
            await logger.LogAsync(jobId, "removed",
                $"{review.DocumentName}: applied {applied.Count} accepted item(s) on finalize",
                ct, groupId: review.DocumentName);
        }

        var outPath = OutputPath(job);
        EpubHandler.WriteUpdatedEpub(job.InputStoragePath, outPath, updates);
        job.OutputStoragePath = outPath;
        job.RemovedCount = totalApplied;
        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await logger.LogAsync(jobId, "summary",
            $"Finalized — applied {totalApplied} accepted item(s) across {updates.Count} chapter(s).",
            ct);
        if (appSettings is not null)
            await DropFolderHelper.TryCopyJobOutputAsync(
                job, appSettings, outPath, logger, users, auth.CurrentValue, ct);
        await logger.UpdateStatusAsync(jobId, JobStatus.Completed, 100, ct: ct);
        return true;
    }

    private string OutputPath(CleanJob job)
    {
        var ext = Path.GetExtension(job.OriginalFileName);
        var stem = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        return Path.Combine(_storage.OutputDirectory, $"{stem}_{job.Id:N}{(ext.Length > 0 ? ext : ".epub")}");
    }
}

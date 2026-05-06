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

    private async Task RunOneAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var logger = sp.GetRequiredService<JobLogger>();
        var openai = sp.GetRequiredService<OpenAiClient>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var auth = sp.GetRequiredService<IOptionsMonitor<AuthOptions>>();
        var bookRepo = sp.GetRequiredService<BookRepo>();
        var cancelRegistry = sp.GetRequiredService<JobCancellationRegistry>();

        var job = await db.CleanJobs.Include(j => j.User)
            .FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null) return;
        // The cancel endpoint marks Canceled in the DB before signaling the
        // token. If the job was canceled while sitting in the queue (so no
        // token existed yet), the row is already Canceled by the time we
        // dequeue — bail without flipping it back to Running.
        if (job.Status == JobStatus.Canceled) return;

        // Register a job-scoped CTS so a cancel request can interrupt the
        // LLM calls below. The token is linked to the worker's stoppingToken
        // (so a server shutdown still cancels) and unregistered on exit.
        using var jobCts = cancelRegistry.Register(jobId, stoppingToken);
        var ct = jobCts.Token;
        try
        {
            await RunInnerAsync(jobId, job, db, logger, openai, users, auth, bookRepo, ct);
        }
        finally
        {
            cancelRegistry.Unregister(jobId);
        }
    }

    private async Task RunInnerAsync(
        Guid jobId, CleanJob job, AppDbContext db, JobLogger logger,
        OpenAiClient openai, UserManager<AppUser> users,
        IOptionsMonitor<AuthOptions> auth, BookRepo bookRepo,
        CancellationToken ct)
    {

        // Capture & clear the rerun flag immediately so a crashed run doesn't
        // loop the worker, and so the review-mode persist branch below can
        // route LLM items to "Pending" instead of "Accepted".
        var isRerun = job.RerunRequested;
        job.RerunRequested = false;
        // Capture & clear the pages filter the same way — single-shot, so a
        // subsequent rerun without an explicit filter goes back to whole-book.
        List<string>? pagesFilter = null;
        if (!string.IsNullOrWhiteSpace(job.PagesFilterJson))
        {
            try { pagesFilter = JsonSerializer.Deserialize<List<string>>(job.PagesFilterJson); }
            catch { /* malformed → treat as whole-book run */ }
        }
        job.PagesFilterJson = null;
        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await logger.UpdateStatusAsync(job.Id, JobStatus.Running, 0, ct: ct);
        if (isRerun)
            await logger.LogAsync(jobId, "info", "Rerun requested — preserving existing review.", ct);

        try
        {
            // Pull the job-owner's per-user prompt addition so we can layer
            // it on top of the admin's prompt for every LLM call below.
            var userPrompt = await db.UserSettings.AsNoTracking()
                .Where(u => u.UserId == job.UserId)
                .Select(u => u.SystemPrompt)
                .FirstOrDefaultAsync(ct);

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

            IReadOnlyList<EpubDocument> docs = EpubHandler.ReadHtmlDocuments(job.InputStoragePath);
            await logger.LogAsync(jobId, "info", $"Found {docs.Count} HTML document(s)", ct);

            // Lazy-init the book repo on the first run so the editor can show
            // pages + diff history. Pre-existing jobs migrated forward get a
            // repo on next run; new jobs get one at upload time (the upload
            // handler primes it). The repo is the source of truth for
            // "current state of pages" once it exists.
            if (string.IsNullOrEmpty(job.RepoPath))
            {
                var pagesForRepo = docs
                    .Select(d => (d.Name, EpubHandler.ExtractText(d.Content)))
                    .ToList();
                job.RepoPath = bookRepo.InitFromPages(job.Id, pagesForRepo);
                await db.SaveChangesAsync(ct);
                await logger.LogAsync(jobId, "info",
                    $"Initialized editor repo with {pagesForRepo.Count} page(s).", ct);
            }

            // Apply the optional per-page filter — restricts processing to
            // just the documents whose side-cars match the requested page
            // paths. Used by the editor's "Run AI on selected pages" button.
            if (pagesFilter is { Count: > 0 } && !string.IsNullOrEmpty(job.RepoPath))
            {
                var allowedDocs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rel in pagesFilter)
                {
                    var docName = bookRepo.ReadDocName(job.RepoPath, rel);
                    if (!string.IsNullOrEmpty(docName)) allowedDocs.Add(docName);
                }
                var filtered = docs.Where(d => allowedDocs.Contains(d.Name)).ToList();
                await logger.LogAsync(jobId, "info",
                    $"Pages filter active — {filtered.Count} of {docs.Count} document(s) selected.", ct);
                docs = filtered;
                if (docs.Count == 0)
                {
                    await logger.LogAsync(jobId, "warn",
                        "No documents matched the requested page paths — nothing to process.", ct);
                    job.Status = JobStatus.Completed;
                    await db.SaveChangesAsync(ct);
                    await logger.UpdateStatusAsync(jobId, JobStatus.Completed, 100, 0, 0, ct);
                    return;
                }
            }

            // Every document goes to the LLM in full. The page-level checkbox
            // UI in the editor replaces patterns as the way to scope a run.
            var queueWork = new List<(EpubDocument Doc, IReadOnlyList<string> Pages)>();
            await logger.LogAsync(jobId, "info", "Sending each chapter to the LLM in full.", ct);
            foreach (var d in docs)
            {
                var text = EpubHandler.ExtractText(d.Content);
                if (!string.IsNullOrWhiteSpace(text))
                    queueWork.Add((d, [text]));
            }

            if (queueWork.Count == 0)
            {
                if (isRerun)
                {
                    // Rerun found nothing new — leave HEAD/working-tree alone.
                    // Pending edits (if any) are surfaced per-page in the
                    // editor; the job-level status doesn't need a special
                    // value to advertise them.
                    job.Status = JobStatus.Completed;
                    await db.SaveChangesAsync(ct);
                    await logger.LogAsync(jobId, "clean", "Rerun: no new matches.", ct);
                    await logger.UpdateStatusAsync(jobId, JobStatus.Completed, 100, 0, 0, ct);
                    return;
                }
                await logger.LogAsync(jobId, "clean", "No matches — copying file unchanged.", ct);
                var unchanged = OutputPath(job);
                File.Copy(job.InputStoragePath, unchanged, overwrite: true);
                job.OutputStoragePath = unchanged;
                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await DropFolderHelper.TryCopyJobOutputAsync(job, appSettings, unchanged, logger, users, auth.CurrentValue, ct);
                await logger.UpdateStatusAsync(jobId, JobStatus.Completed, 100, 0, 0, ct);
                return;
            }

            await logger.LogAsync(jobId, "info",
                $"Sending {queueWork.Count} document(s) to {job.Model}", ct);
            await logger.UpdateStatusAsync(jobId, JobStatus.Running, 0, 0, queueWork.Count, ct);

            var updates = new Dictionary<string, byte[]>();
            // Per-doc list of items the LLM proposed and that matched
            // verbatim — we replay these at the end of the loop to build the
            // post-AI visible text that lands in the editor's working tree.
            // Same lock as `updates` (the worker holds `lock(updates)` across
            // both writes so the two stay consistent without a second lock).
            var proposalsPerDoc = new Dictionary<string, IReadOnlyList<AppliedRemoval>>();
            var totalRemoved = 0;
            var completed = 0;
            var totalCount = queueWork.Count;

            using var sem = new SemaphoreSlim(Math.Max(1, job.MaxWorkers));

            var tasks = queueWork.Select(async work =>
            {
                // Group key for every log line emitted on behalf of this
                // document — used by the UI to keep all retry attempts and
                // ✓/✗ items together even when other docs interleave in time.
                var group = work.Doc.Name;

                await sem.WaitAsync(ct);
                try
                {
                    // Honor pause requests — checked between excerpt tasks so
                    // currently-running LLM calls finish, but no new work
                    // starts until the user resumes.
                    await WaitWhilePausedAsync(jobId, ct);
                    // Up to MaxVerbatimAttempts call+apply attempts. The LLM
                    // sometimes paraphrases the text it claims to remove —
                    // partially or fully — so any item not matched verbatim
                    // triggers another attempt. Each attempt's verbatim
                    // matches are tracked, and we keep the best one across
                    // attempts so a partial-but-good attempt isn't thrown
                    // away by a later worse one.
                    const int MaxVerbatimAttempts = 6;
                    CleanResult lastResult = new(false, [], "");
                    byte[] bestBytes = work.Doc.Content;
                    IReadOnlyList<AppliedRemoval> bestApplied = [];
                    var bestRequested = 0;
                    var bestRawText = "";
                    var attemptsMade = 0;
                    var prevItemsKey = "";

                    for (var attempt = 1; attempt <= MaxVerbatimAttempts; attempt++)
                    {
                        attemptsMade = attempt;
                        lastResult = await openai.IdentifyWatermarksAsync(
                            work.Pages, appSettings.ApiKey!, appSettings.BaseUrl, job.Model, ct,
                            CombinePrompts(appSettings.SystemPrompt, userPrompt, job.SystemPrompt));

                        // Empty / clean response — drop straight to the final
                        // disposition line. No per-attempt log noise.
                        if (!lastResult.HasWatermarks) break;

                        var (bytes, applied) = EpubHandler.ApplyRemovals(work.Doc.Content, lastResult.Items);

                        if (applied.Count > bestApplied.Count)
                        {
                            bestApplied = applied;
                            bestBytes = bytes;
                            bestRequested = lastResult.Items.Count;
                            bestRawText = lastResult.RawText;
                        }

                        // All items matched verbatim → done. Final disposition
                        // line below will report it; no per-attempt line on
                        // the happy path.
                        if (applied.Count == lastResult.Items.Count) break;

                        // Convergence — if the LLM keeps returning the exact
                        // same items, further retries can't improve the
                        // outcome (typically a hallucinated needle that isn't
                        // in the source). Stop wasting API calls.
                        var itemsKey = string.Join("",
                            lastResult.Items.Select(i => i.Remove));
                        if (itemsKey == prevItemsKey) break;
                        prevItemsKey = itemsKey;

                        // Partial. Only emit a per-attempt warn when we're
                        // going to retry — the last failed attempt is folded
                        // into the final disposition line so we don't double up.
                        if (attempt < MaxVerbatimAttempts)
                        {
                            var unmatched = lastResult.Items.Count - applied.Count;
                            await logger.LogAsync(jobId, "warn",
                                $"{work.Doc.Name}: attempt {attempt}/{MaxVerbatimAttempts} — {applied.Count}/{lastResult.Items.Count} matched, {unmatched} unmatched — retrying…",
                                ct, detail: lastResult.RawText, groupId: group);
                        }
                    }

                    if (bestApplied.Count > 0)
                    {
                        lock (updates)
                        {
                            updates[work.Doc.Name] = bestBytes;
                            proposalsPerDoc[work.Doc.Name] = bestApplied;
                        }
                        Interlocked.Add(ref totalRemoved, bestApplied.Count);
                        var unmatched = bestRequested - bestApplied.Count;
                        var isPartial = unmatched > 0;
                        // Different level for partial so the UI can highlight
                        // it distinctly from a full-success "removed" line.
                        var level = isPartial ? "partial" : "removed";
                        var msg = isPartial
                            ? $"{work.Doc.Name}: removed {bestApplied.Count} of {bestRequested} item(s) (after {attemptsMade} attempts, {unmatched} unmatched) — {string.Join(" · ", bestApplied.Select(a => Truncate(a.Removed, 80)))}"
                            : $"{work.Doc.Name}: removed {bestApplied.Count} item(s){(attemptsMade > 1 ? $" (after {attemptsMade} attempts)" : "")} — {string.Join(" · ", bestApplied.Select(a => Truncate(a.Removed, 80)))}";
                        await logger.LogAsync(jobId, level, msg, ct, detail: bestRawText, groupId: group);
                    }
                    else if (lastResult.HasWatermarks)
                    {
                        await logger.LogAsync(jobId, "warn",
                            $"{work.Doc.Name}: LLM flagged but no text matched verbatim after {attemptsMade} attempts — skipping",
                            ct, detail: lastResult.RawText, groupId: group);
                    }
                    else
                    {
                        var note = attemptsMade > 1 ? $" (after {attemptsMade} attempts)" : "";
                        await logger.LogAsync(jobId, "clean",
                            $"{work.Doc.Name}: clean{note}",
                            ct, detail: lastResult.RawText, groupId: group);
                    }
                }
                catch (Exception ex)
                {
                    await logger.LogAsync(jobId, "error", $"{work.Doc.Name}: {ex.Message}", ct, groupId: group);
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

            // Land every doc-with-edits in the working tree. The editor will
            // surface this as a diff against HEAD (= the previous committed
            // state, typically the initial extraction). The user reviews +
            // optionally edits + commits, and finalize exports HEAD as EPUB.
            //
            // A rerun goes through the SAME path even when the original job
            // ran in non-review mode: any edits the user committed in the
            // editor since are preserved (they're in HEAD), the new pass
            // surfaces fresh suggestions on top of HEAD as working-tree
            // changes — so the user explicitly sees what was just found.
            foreach (var work in queueWork)
            {
                if (!proposalsPerDoc.TryGetValue(work.Doc.Name, out var applied)) continue;
                if (applied.Count == 0) continue;
                if (!updates.TryGetValue(work.Doc.Name, out var bytes)) continue;

                // The cleaned chapter HTML lands directly in the working
                // tree — same shape the editor stores at upload time.
                // Pretty-printed so the diff stays readable: same formatting
                // convention as BookImporter's storage path.
                var newHtml = EpubHandler.PrettyPrintHtml(bytes);
                var ok = bookRepo.WritePageByDocName(job.RepoPath!, work.Doc.Name, newHtml);
                if (!ok)
                    await logger.LogAsync(jobId, "warn",
                        $"{work.Doc.Name}: no matching page in editor repo — edit lost",
                        ct, groupId: work.Doc.Name);
            }

            // AI proposals land in the working tree as pending diffs; the
            // editor's per-page indicators surface them. The job itself
            // settles back to Completed — there's no "review pending" job
            // status, just uncommitted-changes visible per-page.
            job.Status = JobStatus.Completed;
            await db.SaveChangesAsync(ct);
            var summary = isRerun
                ? $"Rerun complete — fresh suggestions in the editor across {queueWork.Count} chapter(s)."
                : $"AI run complete — {queueWork.Count} chapter(s), {totalRemoved} suggestion(s) waiting in the editor.";
            await logger.LogAsync(jobId, "summary", summary, ct);
            await logger.UpdateStatusAsync(
                jobId, JobStatus.Completed, 100, totalCount, totalCount, ct);
            // Don't finalize here. AI proposals land in the working tree —
            // committing them now would mean every AI run silently bakes
            // its own suggestions into the cleaned EPUB regardless of what
            // the user accepts. The output is rebuilt from HEAD by each
            // commit endpoint (accept-hunk / accept-page / commit-many),
            // which is the correct trigger.
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

    /// <summary>
    /// Polls the job's status and blocks while it's <see cref="JobStatus.Paused"/>.
    /// Each poll uses its own scope to avoid sharing a DbContext across the
    /// parallel excerpt tasks.
    /// </summary>
    private async Task WaitWhilePausedAsync(Guid jobId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            var freshDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = await freshDb.CleanJobs.AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => (JobStatus?)j.Status)
                .FirstOrDefaultAsync(ct);

            if (status != JobStatus.Paused) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private string OutputPath(CleanJob job)
    {
        var ext = Path.GetExtension(job.OriginalFileName);
        var stem = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        return Path.Combine(_storage.OutputDirectory, $"{stem}_{job.Id:N}{(ext.Length > 0 ? ext : ".epub")}");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// Layer admin → user → novel additions in order so the most specific
    /// directive ends up last (and overrides the broader ones in the
    /// LLM's reading). Empty entries fall away; all empty returns null
    /// so the LLM client uses its built-in default prompt.
    /// </summary>
    private static string? CombinePrompts(params string?[] additions)
    {
        var trimmed = additions
            .Select(s => string.IsNullOrWhiteSpace(s) ? null : s!.Trim())
            .Where(s => s is not null)
            .ToArray();
        return trimmed.Length == 0 ? null : string.Join("\n\n", trimmed);
    }
}

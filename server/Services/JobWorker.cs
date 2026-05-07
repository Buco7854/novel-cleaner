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
        var bookRepo = sp.GetRequiredService<BookRepo>();

        var job = await db.CleanJobs.Include(j => j.User)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        // Capture & clear the rerun flag immediately so a crashed run doesn't
        // loop the worker, and so the review-mode persist branch below can
        // route LLM items to "Pending" instead of "Accepted".
        var isRerun = job.RerunRequested;
        job.RerunRequested = false;
        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await logger.UpdateStatusAsync(job.Id, JobStatus.Running, 0, ct: ct);
        if (isRerun)
            await logger.LogAsync(jobId, "info", "Rerun requested — preserving existing review.", ct);

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

            // Lazy-init the book repo on the first run so the editor can show
            // pages + diff history. Pre-existing jobs migrated forward get a
            // repo on next run; new jobs get one on their first run. The
            // repo is the source of truth for "current state of pages" once
            // it exists.
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
                if (isRerun)
                {
                    // Rerun found nothing new — don't touch the output file or
                    // throw away curated reviews. Just settle back into the
                    // appropriate idle state.
                    var hasReviews = await db.ChapterReviews.AnyAsync(r => r.JobId == jobId, ct);
                    job.Status = hasReviews ? JobStatus.AwaitingReview : JobStatus.Completed;
                    await db.SaveChangesAsync(ct);
                    await logger.LogAsync(jobId, "clean", "Rerun: no new matches.", ct);
                    await logger.UpdateStatusAsync(jobId, job.Status, 100, 0, 0, ct);
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
            // Review mode collects the matchable proposals per chapter so they
            // can be persisted as ReviewProposal rows after the parallel loop.
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
                            work.Excerpts, work.Matched, appSettings.ApiKey!, appSettings.BaseUrl, job.Model, ct,
                            appSettings.SystemPrompt);

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

            // A rerun ALWAYS goes through the review-persist branch (even if
            // the original job ran in non-review mode), so the user gets a
            // chance to review the new findings before they overwrite a
            // previously-cleaned file.
            if (job.ReviewBeforeApplying || isRerun)
            {
                // Load existing reviews (if any) to find-or-create per chapter
                // and to dedupe LLM proposals on rerun. Cheap — bounded by
                // chapters-with-hits.
                var existingReviews = await db.ChapterReviews
                    .Include(r => r.Proposals)
                    .Where(r => r.JobId == job.Id)
                    .ToListAsync(ct);
                var existingByDoc = existingReviews.ToDictionary(r => r.DocumentName);

                var orderIndex = existingReviews.Count == 0
                    ? 0
                    : existingReviews.Max(r => r.OrderIndex) + 1;
                var addedProposals = 0;

                foreach (var work in queueWork)
                {
                    var visibleText = work.Excerpts.Count == 1
                        ? work.Excerpts[0]
                        : string.Join("\n\n", work.Excerpts);

                    if (!existingByDoc.TryGetValue(work.Doc.Name, out var review))
                    {
                        review = new ChapterReview
                        {
                            JobId = job.Id,
                            DocumentName = work.Doc.Name,
                            VisibleText = visibleText,
                            OrderIndex = orderIndex++,
                        };
                        db.ChapterReviews.Add(review);
                    }

                    if (!proposalsPerDoc.TryGetValue(work.Doc.Name, out var applied)) continue;

                    // Dedupe: don't re-add an LLM proposal whose verbatim text
                    // already exists for this chapter. Avoids accumulating
                    // dupes on every rerun.
                    var existingTexts = new HashSet<string>(
                        review.Proposals
                            .Where(p => p.Source == ProposalSource.Llm)
                            .Select(p => p.Text),
                        StringComparer.Ordinal);
                    foreach (var a in applied)
                    {
                        if (!existingTexts.Add(a.Removed)) continue;
                        review.Proposals.Add(new ReviewProposal
                        {
                            Text = a.Removed,
                            Reason = a.Reason,
                            Source = ProposalSource.Llm,
                            // First pass on a fresh job: accept by default
                            // (review = "veto unwanted removals"). Rerun: the
                            // user has already curated the original set, so
                            // surface new findings as Pending — they must
                            // explicitly approve them.
                            Decision = isRerun
                                ? ProposalDecision.Pending
                                : ProposalDecision.Accepted,
                        });
                        addedProposals++;
                    }
                }

                job.Status = JobStatus.AwaitingReview;
                await db.SaveChangesAsync(ct);
                var summary = isRerun
                    ? $"Rerun complete — {addedProposals} new proposal(s) pending across {queueWork.Count} chapter(s)."
                    : $"Awaiting your review — {queueWork.Count} chapter(s), {addedProposals} proposal(s).";
                await logger.LogAsync(jobId, "summary", summary, ct);
                await logger.UpdateStatusAsync(
                    jobId, JobStatus.AwaitingReview, 100, totalCount, totalCount, ct);
                return;
            }

            var outPath = OutputPath(job);
            EpubHandler.WriteUpdatedEpub(job.InputStoragePath, outPath, updates);
            job.OutputStoragePath = outPath;
            job.RemovedCount = totalRemoved;
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await logger.LogAsync(jobId, "summary",
                $"Done — removed {totalRemoved} watermark item(s).", ct);
            await DropFolderHelper.TryCopyJobOutputAsync(job, appSettings, outPath, logger, users, auth.CurrentValue, ct);
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
}

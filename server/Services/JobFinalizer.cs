using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Builds the cleaned EPUB from the job's editor repo. The committed HEAD of
/// each page is the source of truth for "what should the cleaned book look
/// like"; we diff each page against its initial-commit state (raw extraction)
/// to recover the removal set, then re-apply that set to the original HTML so
/// formatting and markup survive.
///
/// User edits that *insert* new text are dropped on export with a log line:
/// the EPUB exporter is delete-only by design (preserving the original
/// markup), and producing inserted markup from plain text is out of scope.
/// </summary>
public sealed class JobFinalizer(
    AppDbContext db,
    JobLogger logger,
    BookRepo repos,
    UserManager<AppUser> users,
    IOptionsMonitor<AuthOptions> auth,
    IOptions<StorageOptions> storage)
{
    private readonly StorageOptions _storage = storage.Value;

    public async Task<bool> FinalizeAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.CleanJobs
            .Include(j => j.User)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return false;
        if (string.IsNullOrEmpty(job.RepoPath))
        {
            await logger.LogAsync(jobId, "error",
                "Cannot finalize: editor repo not initialized for this job.", ct);
            return false;
        }

        // A finalize from the editor lands here when the job is
        // AwaitingReview; the worker's auto-mode lands here straight from
        // Running. Both should be allowed.
        if (job.Status is not (JobStatus.AwaitingReview or JobStatus.Running))
            return false;

        var appSettings = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == AppSettings.SingletonKey, ct);

        // Snapshot any uncommitted edits before we read HEAD — finalize
        // should reflect what the user *sees*, not the last commit they
        // happened to make. Idempotent when nothing's dirty.
        repos.Commit(job.RepoPath, "Finalize snapshot");

        var docs = EpubHandler.ReadHtmlDocuments(job.InputStoragePath)
            .ToDictionary(d => d.Name, d => d.Content);

        var updates = new Dictionary<string, byte[]>();
        var totalApplied = 0;
        var totalInserts = 0;

        foreach (var page in repos.ListPages(job.RepoPath))
        {
            var docName = repos.ReadDocName(job.RepoPath, page.Path);
            if (docName is null || !docs.TryGetValue(docName, out var html))
            {
                await logger.LogAsync(jobId, "warn",
                    $"{page.Path}: no original-doc mapping — skipping",
                    ct, groupId: page.Path);
                continue;
            }

            var initial = repos.ReadInitialContent(job.RepoPath, page.Path);
            var head = repos.ReadHeadContent(job.RepoPath, page.Path);
            if (initial == head) continue; // no edits to apply for this page

            var (removed, inserted) = ParagraphDiff(initial, head);
            if (inserted.Count > 0)
            {
                totalInserts += inserted.Count;
                await logger.LogAsync(jobId, "warn",
                    $"{docName}: {inserted.Count} inserted paragraph(s) dropped on export — EPUB writer is delete-only",
                    ct, groupId: docName);
            }
            if (removed.Count == 0) continue;

            var instructions = removed
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => new RemovalInstruction(r, "Editor"))
                .ToList();

            var (bytes, applied) = EpubHandler.ApplyRemovals(html, instructions);
            if (applied.Count == 0) continue;

            updates[docName] = bytes;
            totalApplied += applied.Count;
            await logger.LogAsync(jobId, "removed",
                $"{docName}: applied {applied.Count} removal(s) on finalize",
                ct, groupId: docName);
        }

        var outPath = OutputPath(job);
        EpubHandler.WriteUpdatedEpub(job.InputStoragePath, outPath, updates);
        job.OutputStoragePath = outPath;
        job.RemovedCount = totalApplied;
        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var summary = totalInserts > 0
            ? $"Finalized — applied {totalApplied} removal(s) across {updates.Count} chapter(s); dropped {totalInserts} user-inserted paragraph(s)."
            : $"Finalized — applied {totalApplied} removal(s) across {updates.Count} chapter(s).";
        await logger.LogAsync(jobId, "summary", summary, ct);
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

    /// <summary>
    /// Paragraph-level LCS diff between two visible-text snapshots of one
    /// page. Returns the paragraphs present in <paramref name="initial"/>
    /// but absent from <paramref name="head"/> (= what the user/AI removed)
    /// and the paragraphs added in <paramref name="head"/> (= user inserts,
    /// dropped on export).
    ///
    /// We split on ASCII newlines because <c>EpubHandler.ExtractText</c>
    /// preserves block-level boundaries that way. Falls back to the entire
    /// text as a single paragraph when there are no newlines, which yields
    /// a coarse but correct "delete-the-whole-page-or-not" decision.
    /// </summary>
    internal static (List<string> Removed, List<string> Inserted) ParagraphDiff(string initial, string head)
    {
        var a = initial.Split('\n');
        var b = head.Split('\n');
        var m = a.Length;
        var n = b.Length;

        // Standard LCS DP. Bounded by the number of paragraphs per page,
        // typically <500 — easily fits in memory and runs in microseconds.
        var dp = new int[m + 1, n + 1];
        for (var i = 1; i <= m; i++)
        for (var j = 1; j <= n; j++)
            dp[i, j] = a[i - 1] == b[j - 1]
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var removed = new List<string>();
        var inserted = new List<string>();
        var ii = m;
        var jj = n;
        while (ii > 0 || jj > 0)
        {
            if (ii > 0 && jj > 0 && a[ii - 1] == b[jj - 1])
            {
                ii--; jj--;
            }
            else if (jj > 0 && (ii == 0 || dp[ii, jj - 1] >= dp[ii - 1, jj]))
            {
                inserted.Insert(0, b[jj - 1]);
                jj--;
            }
            else
            {
                removed.Insert(0, a[ii - 1]);
                ii--;
            }
        }
        return (removed, inserted);
    }
}

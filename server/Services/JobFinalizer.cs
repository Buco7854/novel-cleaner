using System.Text;
using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Builds the cleaned EPUB from the job's editor repo. Each page in the repo
/// holds the chapter's raw HTML; finalize copies the input EPUB and overlays
/// each page's HEAD content over its source entry. Pages that match the
/// initial commit byte-for-byte are skipped — the original entry is left in
/// place so we never re-write a chapter that wasn't touched.
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

        // Allow finalize from any non-active state. The download endpoint
        // calls us lazily whenever the user clicks Download, regardless of
        // whether the AI has run yet — for an Idle job that just means
        // "rebuild output from input" (no edits) which is what the user
        // wants when they edit metadata and download.
        if (job.Status is JobStatus.Queued or JobStatus.Paused)
            return false;

        var appSettings = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == AppSettings.SingletonKey, ct);

        // We deliberately do NOT auto-commit the working tree here. AI
        // proposals (and any in-progress user edits) live in the working
        // tree exactly because the user hasn't accepted them yet —
        // snapshotting on finalize would silently bake every uncommitted
        // proposal into the output, which is the opposite of what
        // "accept what I want, then download" means.

        var updates = new Dictionary<string, byte[]>();
        var totalChanged = 0;

        foreach (var page in repos.ListPages(job.RepoPath))
        {
            var docName = repos.ReadDocName(job.RepoPath, page.Path);
            if (docName is null)
            {
                await logger.LogAsync(jobId, "warn",
                    $"{page.Path}: no original-doc mapping — skipping",
                    ct, groupId: page.Path);
                continue;
            }

            var initial = repos.ReadInitialContent(job.RepoPath, page.Path);
            var head = repos.ReadHeadContent(job.RepoPath, page.Path);
            if (initial == head) continue; // chapter unchanged — leave EPUB entry as-is

            // Repo stores pretty-printed HTML for editor readability; strip
            // the indent-only whitespace before writing the EPUB so the
            // file doesn't bloat. Text-content whitespace inside elements
            // is preserved by MinifyMarkupFormatter.
            updates[docName] = EpubHandler.MinifyHtml(head);
            totalChanged++;
        }

        var outPath = OutputPath(job);
        EpubHandler.WriteUpdatedEpub(job.InputStoragePath, outPath, updates);
        job.OutputStoragePath = outPath;
        job.RemovedCount = totalChanged;
        // Leave Idle alone — the lazy-finalize-on-download path runs the
        // finalizer for any job, and an Idle job that's never run AI shouldn't
        // get bumped to Completed just because the user downloaded a copy.
        // The Running path (worker auto-mode) and any Failed/Canceled re-runs
        // legitimately settle to Completed here.
        if (job.Status != JobStatus.Idle)
        {
            job.Status = JobStatus.Completed;
            job.CompletedAt ??= DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        if (totalChanged > 0)
            await logger.LogAsync(jobId, "summary",
                $"Finalized — {totalChanged} chapter(s) updated.", ct);

        if (appSettings is not null)
            await DropFolderHelper.TryCopyJobOutputAsync(
                job, appSettings, outPath, logger, users, auth.CurrentValue, ct);
        if (job.Status == JobStatus.Completed)
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

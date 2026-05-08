using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Builds the cleaned EPUB from the novel's editor repo. Each page in the
/// repo holds the chapter's raw HTML; finalize copies the input EPUB and
/// overlays each page's HEAD content over its source entry. Pages that match
/// the initial commit byte-for-byte are skipped — the original entry is left
/// in place so we never re-write a chapter that wasn't touched.
/// </summary>
public sealed class NovelFinalizer(
    AppDbContext db,
    NovelEventLogger logger,
    NovelEditorRepo editorRepo,
    UserManager<AppUser> users,
    IOptionsMonitor<AuthOptions> auth,
    IOptions<StorageOptions> storage)
{
    private readonly StorageOptions _storage = storage.Value;

    public async Task<bool> FinalizeAsync(Guid novelId, CancellationToken ct = default)
    {
        var novel = await db.Novels
            .Include(n => n.User)
            .FirstOrDefaultAsync(n => n.Id == novelId, ct);
        if (novel is null) return false;
        if (string.IsNullOrEmpty(novel.RepoPath))
        {
            await logger.LogAsync(novelId, "error",
                "Cannot finalize: editor repo not initialized for this novel.", ct);
            return false;
        }

        // Allow finalize from any non-active state. The download endpoint
        // calls us lazily whenever the user clicks Download, regardless of
        // whether the AI has run yet — for an Idle novel that just means
        // "rebuild output from input" (no edits) which is what the user
        // wants when they edit metadata and download.
        if (novel.Status is NovelStatus.Queued or NovelStatus.Paused)
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

        foreach (var page in editorRepo.ListPages(novel.RepoPath))
        {
            var docName = editorRepo.ReadDocName(novel.RepoPath, page.Path);
            if (docName is null)
            {
                await logger.LogAsync(novelId, "warn",
                    $"{page.Path}: no original-doc mapping — skipping",
                    ct, groupId: page.Path);
                continue;
            }

            var initial = editorRepo.ReadInitialContent(novel.RepoPath, page.Path);
            var head = editorRepo.ReadHeadContent(novel.RepoPath, page.Path);
            if (initial == head) continue; // chapter unchanged — leave EPUB entry as-is

            // Repo stores pretty-printed HTML for editor readability; strip
            // the indent-only whitespace before writing the EPUB so the
            // file doesn't bloat. Text-content whitespace inside elements
            // is preserved by MinifyMarkupFormatter.
            updates[docName] = EpubHandler.MinifyHtml(head);
            totalChanged++;
        }

        var outPath = OutputPath(novel);
        EpubHandler.WriteUpdatedEpub(novel.InputStoragePath, outPath, updates);
        novel.OutputStoragePath = outPath;
        novel.RemovedCount = totalChanged;
        // Leave Idle alone — the lazy-finalize-on-download path runs the
        // finalizer for any novel, and an Idle novel that's never run AI
        // shouldn't get bumped to Completed just because the user downloaded
        // a copy. The Running path (worker auto-mode) and any Failed/Canceled
        // re-runs legitimately settle to Completed here.
        if (novel.Status != NovelStatus.Idle)
        {
            novel.Status = NovelStatus.Completed;
            novel.CompletedAt ??= DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        if (totalChanged > 0)
            await logger.LogAsync(novelId, "summary",
                $"Finalized — {totalChanged} chapter(s) updated.", ct);

        if (appSettings is not null)
            await DropFolderHelper.TryCopyOutputAsync(
                novel, appSettings, outPath, logger, users, auth.CurrentValue, ct);
        if (novel.Status == NovelStatus.Completed)
            await logger.UpdateStatusAsync(novelId, NovelStatus.Completed, 100, ct: ct);
        return true;
    }

    private string OutputPath(Novel novel)
    {
        var ext = Path.GetExtension(novel.OriginalFileName);
        var stem = Path.GetFileNameWithoutExtension(novel.OriginalFileName);
        return Path.Combine(_storage.OutputDirectory, $"{stem}_{novel.Id:N}{(ext.Length > 0 ? ext : ".epub")}");
    }
}

using Tergeo.Server.Configuration;
using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Tergeo.Server.Services;

/// <summary>
/// Builds the cleaned EPUB from the book's editor repo. Each page in the
/// repo holds the chapter's raw HTML; finalize copies the input EPUB and
/// overlays each page's HEAD content over its source entry. Pages that match
/// the initial commit byte-for-byte are skipped — the original entry is left
/// in place so we never re-write a chapter that wasn't touched.
/// </summary>
public sealed class BookFinalizer(
    AppDbContext db,
    BookEventLogger logger,
    BookEditorRepo editorRepo,
    AppSettingsResolver settings,
    UserManager<AppUser> users,
    IOptionsMonitor<AuthOptions> auth,
    IOptions<StorageOptions> storage)
{
    private readonly StorageOptions _storage = storage.Value;

    public async Task<bool> FinalizeAsync(Guid bookId, CancellationToken ct = default)
    {
        var book = await db.Books
            .Include(n => n.User)
            .FirstOrDefaultAsync(n => n.Id == bookId, ct);
        if (book is null) return false;
        if (string.IsNullOrEmpty(book.RepoPath))
        {
            await logger.LogAsync(bookId, "error",
                "Cannot finalize: editor repo not initialized for this book.", ct);
            return false;
        }

        // Allow finalize from any non-active state. The download endpoint
        // calls us lazily whenever the user clicks Download, regardless of
        // whether the AI has run yet — for an Idle book that just means
        // "rebuild output from input" (no edits) which is what the user
        // wants when they edit metadata and download.
        if (book.Status is BookStatus.Queued or BookStatus.Paused)
            return false;

        var appSettings = await settings.ResolveAsync(ct);

        // We deliberately do NOT auto-commit the working tree here. AI
        // proposals (and any in-progress user edits) live in the working
        // tree exactly because the user hasn't accepted them yet —
        // snapshotting on finalize would silently bake every uncommitted
        // proposal into the output, which is the opposite of what
        // "accept what I want, then download" means.

        var updates = new Dictionary<string, byte[]>();
        var totalChanged = 0;

        foreach (var page in editorRepo.ListPages(book.RepoPath))
        {
            var docName = editorRepo.ReadDocName(book.RepoPath, page.Path);
            if (docName is null)
            {
                await logger.LogAsync(bookId, "warn",
                    $"{page.Path}: no original-doc mapping — skipping",
                    ct, groupId: page.Path);
                continue;
            }

            var initial = editorRepo.ReadInitialContent(book.RepoPath, page.Path);
            var head = editorRepo.ReadHeadContent(book.RepoPath, page.Path);
            if (initial == head) continue; // chapter unchanged — leave EPUB entry as-is

            // Repo stores pretty-printed HTML for editor readability; strip
            // the indent-only whitespace before writing the EPUB so the
            // file doesn't bloat. Text-content whitespace inside elements
            // is preserved by MinifyMarkupFormatter.
            updates[docName] = EpubHandler.MinifyHtml(head);
            totalChanged++;
        }

        var outPath = OutputPath(book);
        EpubHandler.WriteUpdatedEpub(book.InputStoragePath, outPath, updates);
        book.OutputStoragePath = outPath;
        book.RemovedCount = totalChanged;
        // Leave Idle alone — the lazy-finalize-on-download path runs the
        // finalizer for any book, and an Idle book that's never run AI
        // shouldn't get bumped to Completed just because the user downloaded
        // a copy. The Running path (worker auto-mode) and any Failed/Canceled
        // re-runs legitimately settle to Completed here.
        if (book.Status != BookStatus.Idle)
        {
            book.Status = BookStatus.Completed;
            book.CompletedAt ??= DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        if (totalChanged > 0)
            await logger.LogAsync(bookId, "summary",
                $"Finalized — {totalChanged} chapter(s) updated.", ct);

        await DropFolderHelper.TryCopyOutputAsync(
            book, appSettings.DropFolder, outPath, logger, users, auth.CurrentValue, ct);
        if (book.Status == BookStatus.Completed)
            await logger.UpdateStatusAsync(bookId, BookStatus.Completed, 100, ct: ct);
        return true;
    }

    private string OutputPath(Book book)
    {
        var ext = Path.GetExtension(book.OriginalFileName);
        var stem = Path.GetFileNameWithoutExtension(book.OriginalFileName);
        return Path.Combine(_storage.OutputDirectory, $"{stem}_{book.Id:N}{(ext.Length > 0 ? ext : ".epub")}");
    }
}

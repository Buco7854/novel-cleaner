using System.Security.Claims;
using System.Text.Json;
using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Tergeo.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tergeo.Server.Endpoints;

/// <summary>
/// Book-level actions that aren't tied to a specific page (export, rerun
/// the AI pass, clone-and-reprocess). Per-page editor surface lives on
/// <see cref="PagesEndpoints"/>; the older review-list surface
/// (<c>/api/books/{id}/reviews/*</c>) was removed when the editor switched
/// to the git-backed model.
/// </summary>
public static class BookActionsEndpoints
{
    public static void MapBookActionsEndpoints(this IEndpointRouteBuilder app)
    {
        // Apply the editor's HEAD to the original EPUB and write the cleaned
        // output. Mirrors what the auto-mode pass produces, but driven by the
        // user's explicit accept of the current review state.
        app.MapPost("/api/books/{bookId:guid}/finalize", async (
            Guid bookId, HttpContext http, AppDbContext db, BookFinalizer finalizer,
            CancellationToken ct) =>
        {
            if (await GetOwnedBookAsync(db, http, bookId) is null) return Results.NotFound();
            var ok = await finalizer.FinalizeAsync(bookId, ct);
            if (!ok) return Results.BadRequest(new { error = "Book is not awaiting review or has no editor repo." });
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Run the LLM pass against this book, optionally restricted to a
        // subset of page paths. Replaces the older "rerun-ai" endpoint —
        // handles both fresh runs (Idle books that have never been
        // processed) and reruns (Completed/AwaitingReview/Failed/Canceled).
        //
        // For reruns, fresh suggestions land in the editor's working tree on
        // top of the user's already-committed edits and surface as a diff
        // vs HEAD; for fresh runs, the worker behaves exactly as it would on
        // first upload (was previously the auto-enqueue path).
        //
        // Body: <c>{ "pages": ["pages/0001_foo.txt", ...] }</c> — optional;
        // when omitted or empty, the run covers the whole book.
        app.MapPost("/api/books/{bookId:guid}/run-ai", async (
            Guid bookId, HttpContext http, AppDbContext db, BookProcessingQueue queue,
            AppSettingsResolver settings,
            [FromBody] RunAiDto? dto, CancellationToken ct) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (book.Status is BookStatus.Running or BookStatus.Queued or BookStatus.Paused)
                return Results.BadRequest(new { error = "Book is already running. Wait for it to finish." });
            if (!File.Exists(book.InputStoragePath))
                return Results.BadRequest(new { error = "Original file is missing." });

            // Honor the admin master-switch — refuse to enqueue when the
            // AI feature is turned off, even if the user crafts the POST
            // by hand.
            var resolved = await settings.ResolveAsync(ct);
            if (!resolved.AiEnabled)
                return Results.BadRequest(new { error = "AI features have been disabled by an admin." });

            // RerunRequested triggers the worker's "preserve existing review
            // state" path — only correct when the book has been processed
            // before. A fresh Idle book follows the standard first-run flow.
            book.RerunRequested = book.Status is not BookStatus.Idle;
            book.PagesFilterJson = dto?.Pages is { Count: > 0 }
                ? JsonSerializer.Serialize(dto.Pages)
                : null;
            book.Status = BookStatus.Queued;
            book.ErrorMessage = null;
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(book.Id, ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Clone a completed book — copy its cleaned output into a fresh
        // book entry that the user can run the AI on (or further edit)
        // independently. Lands in Idle status; the user explicitly clicks
        // Run AI on the new entry when ready, same as any fresh upload.
        app.MapPost("/api/books/{bookId:guid}/clone", async (
            Guid bookId, HttpContext http, AppDbContext db, BookImporter importer,
            AppSettingsResolver settings,
            CancellationToken ct) =>
        {
            var book = await GetOwnedBookAsync(db, http, bookId);
            if (book is null) return Results.NotFound();
            if (book.OutputStoragePath is null || !File.Exists(book.OutputStoragePath))
                return Results.BadRequest(new { error = "Book has no cleaned output to clone." });

            var appS = await settings.ResolveAsync(ct);
            if (string.IsNullOrWhiteSpace(appS.Model))
                return Results.BadRequest(new { error = "Global LLM settings not configured." });

            // Copy the output to a fresh upload-area path so the clone is
            // independent of the source book's storage lifecycle.
            var newId = Guid.NewGuid();
            var uploadDir = Path.GetDirectoryName(book.InputStoragePath)!;
            Directory.CreateDirectory(uploadDir);
            var newInput = Path.Combine(uploadDir, $"{newId:N}_{Path.GetFileName(book.OriginalFileName)}");
            File.Copy(book.OutputStoragePath, newInput);

            // Prime the editor repo + read metadata, same as a normal upload.
            var (repoPath, meta) = await importer.ImportAsync(newId, newInput, ct);

            var clone = new Book
            {
                Id = newId,
                UserId = book.UserId,
                OriginalFileName = book.OriginalFileName,
                FileSizeBytes = new FileInfo(newInput).Length,
                InputStoragePath = newInput,
                RepoPath = repoPath,
                Title       = meta.Title,
                Author      = meta.Author,
                Language    = meta.Language,
                Publisher   = meta.Publisher,
                Description = meta.Description,
                Status = BookStatus.Idle,
                Model = appS.Model,
            };
            db.Books.Add(clone);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = clone.Id });
        }).RequireAuthorization();
    }

    private static Guid GetUserId(HttpContext http)
        => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<Book?> GetOwnedBookAsync(AppDbContext db, HttpContext http, Guid id)
    {
        var userId = GetUserId(http);
        var isAdmin = http.User.IsInRole(AppRoles.Admin);
        return isAdmin
            ? await db.Books.FirstOrDefaultAsync(n => n.Id == id)
            : await db.Books.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
    }

    public sealed record RunAiDto(List<string>? Pages);
}

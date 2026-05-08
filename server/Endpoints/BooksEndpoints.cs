using System.Security.Claims;
using Tergeo.Server.Configuration;
using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Tergeo.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Tergeo.Server.Endpoints;

/// <summary>
/// CRUD + lifecycle endpoints for books (the EPUBs in a user's library).
/// Mounted at <c>/api/books</c>; the per-page editor surface lives in
/// <see cref="PagesEndpoints"/> and the cross-book actions (finalize, run-AI,
/// clone) in <see cref="ReviewsEndpoints"/>.
/// </summary>
public static class BooksEndpoints
{
    public static void MapBooksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/books").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, AppDbContext db, BookEditorRepo editorRepo,
            [FromQuery] int page = 1, [FromQuery] int size = 20) =>
        {
            size = Math.Clamp(size, 1, 100);
            page = Math.Max(1, page);
            var userId = GetUserId(http);
            var q = db.Books.AsNoTracking().Where(n => n.UserId == userId);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(n => n.CreatedAt)
                .Skip((page - 1) * size)
                .Take(size)
                .Select(n => new
                {
                    n.Id, n.OriginalFileName, n.Title, n.Author,
                    n.FileSizeBytes, n.Status, n.Model, n.RemovedCount,
                    n.CreatedAt, n.CompletedAt, n.OutputStoragePath, n.RepoPath,
                }).ToListAsync();

            // Derive the same simplified status as the detail endpoint —
            // cheap libgit2 status call, runs at most 100 times per page.
            var items = rows.Select(n =>
            {
                var status = DeriveListDisplayStatus(n.Status, n.RepoPath, editorRepo);
                return new
                {
                    id = n.Id,
                    fileName = n.OriginalFileName,
                    title = n.Title,
                    author = n.Author,
                    sizeBytes = n.FileSizeBytes,
                    status = status.ToString(),
                    model = n.Model,
                    removed = n.RemovedCount,
                    createdAt = n.CreatedAt,
                    completedAt = n.CompletedAt,
                    hasOutput = n.OutputStoragePath != null,
                };
            }).ToList();
            return Results.Ok(new { items, total, page, size });
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext http,
            AppDbContext db,
            AppSettingsResolver settings,
            BookEditorRepo editorRepo) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            var logs = await db.BookLogs.AsNoTracking()
                .Where(l => l.BookId == id).OrderBy(l => l.Id).Take(2000)
                .Select(l => new { l.Timestamp, l.Level, l.Message, l.Detail, l.GroupId })
                .ToListAsync();

            var appSettings = await settings.ResolveAsync();
            var dropConfigured = !string.IsNullOrWhiteSpace(appSettings.DropFolder);

            // dropConfigured is what the editor's "Push to folder" button now
            // gates on: as soon as an admin sets a drop folder, the button
            // shows. The actual permission check still runs server-side at
            // POST /drop time and returns 403 if the user lacks DropFolder —
            // that error surfaces as a toast, which is a clearer UX than
            // silently hiding the button.

            // The user-facing status set is intentionally tiny:
            //   Queued / Running / Paused  ← active worker states (raw)
            //   AwaitingReview             ← repo has uncommitted changes
            //   Idle                       ← everything else
            // Completed / Failed / Canceled aren't surfaced — the log panel
            // tells you what happened, the pill just answers "is there
            // anything for me to review?".
            var status = DeriveDisplayStatus(book, editorRepo);

            return Results.Ok(new
            {
                id = book.Id,
                fileName = book.OriginalFileName,
                title       = book.Title,
                author      = book.Author,
                language    = book.Language,
                publisher   = book.Publisher,
                description = book.Description,
                status = status.ToString(),
                model = book.Model,
                removed = book.RemovedCount,
                error = book.ErrorMessage,
                createdAt = book.CreatedAt,
                startedAt = book.StartedAt,
                completedAt = book.CompletedAt,
                hasOutput = book.OutputStoragePath != null,
                dropConfigured,
                systemPrompt = book.SystemPrompt ?? "",
                logs,
            });
        });

        group.MapPost("/{id:guid}/drop", async (
            Guid id,
            HttpContext http,
            AppDbContext db,
            BookEventLogger logger,
            AppSettingsResolver settings,
            UserManager<AppUser> userManager,
            IOptionsMonitor<AuthOptions> authOpts) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            if (book.OutputStoragePath is null || !File.Exists(book.OutputStoragePath))
                return Results.BadRequest(new { error = "Book has no output file." });

            var appSettings = await settings.ResolveAsync();
            if (string.IsNullOrWhiteSpace(appSettings.DropFolder))
                return Results.BadRequest(new { error = "No drop folder configured in settings." });

            // Permission is keyed to the NOVEL OWNER, not the caller — admins
            // viewing another user's book shouldn't be able to push into the
            // drop folder on behalf of a non-permitted user.
            var owner = await userManager.FindByIdAsync(book.UserId.ToString());
            if (owner is null
                || !await Permissions.CanUseDropFolderAsync(userManager, owner, authOpts.CurrentValue))
                return Results.Forbid();

            try
            {
                var result = DropFolderHelper.Copy(appSettings.DropFolder, book.OriginalFileName, book.OutputStoragePath);
                await logger.LogAsync(book.Id, "info", $"Manually copied to drop folder: {result.DestinationPath}");
                return Results.Ok(new { destination = result.DestinationPath });
            }
            catch (Exception ex)
            {
                await logger.LogAsync(book.Id, "warn", $"Manual drop folder copy failed: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        group.MapGet("/{id:guid}/download", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null || book.OutputStoragePath is null || !File.Exists(book.OutputStoragePath))
                return Results.NotFound();
            var stem = Path.GetFileNameWithoutExtension(book.OriginalFileName);
            return Results.File(book.OutputStoragePath, "application/epub+zip", $"{stem}_cleaned.epub");
        });

        group.MapPost("/", async (
            HttpContext http,
            AppDbContext db,
            BookImporter importer,
            AppSettingsResolver settings,
            IOptions<StorageOptions> storage) =>
        {
            // Multipart form posts are "simple" CORS requests, so a malicious
            // cross-origin page can submit one with the user's cookie.
            // Require a non-simple custom header so any cross-origin request
            // triggers a CORS preflight, which we don't allow.
            if (!http.Request.Headers.TryGetValue("X-Requested-With", out _))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var userId = GetUserId(http);
            var appS = await settings.ResolveAsync();
            if (string.IsNullOrWhiteSpace(appS.Model))
                return Results.BadRequest(new { error = "Global LLM settings have not been configured by an admin yet." });

            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await http.Request.ReadFormAsync();
            var files = form.Files.Where(f => f.Length > 0).ToList();
            if (files.Count == 0) return Results.BadRequest(new { error = "No files uploaded" });

            Directory.CreateDirectory(storage.Value.UploadDirectory);

            var created = new List<Guid>();
            foreach (var file in files)
            {
                if (file.Length > storage.Value.MaxUploadBytes)
                    return Results.BadRequest(new { error = $"{file.FileName}: too large" });

                var id = Guid.NewGuid();
                var safeName = Path.GetFileName(file.FileName);
                var inputPath = Path.Combine(storage.Value.UploadDirectory, $"{id:N}_{safeName}");
                await using (var stream = File.Create(inputPath))
                    await file.CopyToAsync(stream);

                // Initialize the editor's git repo and read OPF metadata in
                // one call — see BookImporter for the shared pipeline.
                var (repoPath, meta) = await importer.ImportAsync(id, inputPath);

                var book = new Book
                {
                    Id = id,
                    UserId = userId,
                    OriginalFileName = safeName,
                    FileSizeBytes = file.Length,
                    InputStoragePath = inputPath,
                    RepoPath = repoPath,
                    Title       = meta.Title,
                    Author      = meta.Author,
                    Language    = meta.Language,
                    Publisher   = meta.Publisher,
                    Description = meta.Description,
                    // Idle = uploaded but no AI run yet. Editor is usable;
                    // the user kicks off processing from the toolbar.
                    Status = BookStatus.Idle,
                    // Admin-managed (AppSettings)
                    Model = appS.Model,
                };
                db.Books.Add(book);
                created.Add(id);
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { ids = created });
        }).DisableAntiforgery();

        // Update the per-book AI instructions. Saves to the row's
        // SystemPrompt column; the worker layers this on top of the admin
        // and user prompts at LLM call time.
        group.MapPut("/{id:guid}/prompt", async (
            Guid id, HttpContext http, AppDbContext db, [FromBody] PromptDto dto) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            book.SystemPrompt = string.IsNullOrWhiteSpace(dto.SystemPrompt)
                ? null : dto.SystemPrompt.Trim();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Update the EPUB metadata. We persist into the DB (drives the UI's
        // displayed title/author everywhere) AND rewrite the OPF inside the
        // INPUT EPUB — finalize/download both flow from input → output, so
        // the next download picks up the edits without a separate re-export
        // step. The OPF write is best-effort; a failure surfaces as a warning
        // log line but does not roll back the DB save.
        group.MapPut("/{id:guid}/metadata", async (
            Guid id, HttpContext http, AppDbContext db, BookEventLogger logger, [FromBody] MetadataDto dto) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();

            string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            book.Title       = Norm(dto.Title);
            book.Author      = Norm(dto.Author);
            book.Language    = Norm(dto.Language);
            book.Publisher   = Norm(dto.Publisher);
            book.Description = Norm(dto.Description);
            await db.SaveChangesAsync();

            // Write to BOTH the input and the output EPUB. The download
            // endpoint serves OutputStoragePath after a finalize — without
            // this second write a metadata edit made post-finalize would
            // appear in the editor's title but not in the file the user
            // actually downloads.
            var meta = new EpubMetadata(
                book.Title, book.Author, book.Language, book.Publisher, book.Description);
            foreach (var path in new[] { book.InputStoragePath, book.OutputStoragePath })
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                try { EpubHandler.WriteMetadata(path, meta); }
                catch (Exception ex)
                {
                    await logger.LogAsync(book.Id, "warn",
                        $"Could not write metadata into {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            return Results.Ok(new
            {
                title       = book.Title,
                author      = book.Author,
                language    = book.Language,
                publisher   = book.Publisher,
                description = book.Description,
            });
        });

        // Wipe the persisted log lines for a book. The editor's "Clear"
        // button calls this so a refresh doesn't repopulate the panel from
        // server history. Live SignalR feed continues for any new lines
        // emitted by an in-flight run.
        group.MapDelete("/{id:guid}/logs", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            await db.BookLogs.Where(l => l.BookId == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/pause", async (Guid id, HttpContext http, AppDbContext db, BookEventLogger logger) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            if (book.Status != BookStatus.Running) return Results.BadRequest(new { error = "Only running books can be paused." });
            book.Status = BookStatus.Paused;
            await db.SaveChangesAsync();
            await logger.LogAsync(book.Id, "info", "Pause requested — no new excerpts will start until resumed.");
            await logger.UpdateStatusAsync(book.Id, BookStatus.Paused);
            return Results.Ok(new { ok = true });
        });

        // Cancel a Queued/Running/Paused run. Two paths converge here:
        // - Queued: nothing is running yet, so we just flip the DB to
        //   Canceled. The worker checks this before starting.
        // - Running/Paused: same DB flip, plus we signal the registered
        //   CTS so the in-flight LLM calls actually interrupt. Without
        //   the signal a Running book would keep firing requests until
        //   the chapter loop naturally exits.
        group.MapPost("/{id:guid}/cancel", async (
            Guid id, HttpContext http, AppDbContext db, BookEventLogger logger,
            BookCancellationRegistry cancelRegistry) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            if (book.Status is not (BookStatus.Queued or BookStatus.Running or BookStatus.Paused))
                return Results.BadRequest(new { error = "Only queued, running, or paused books can be canceled." });

            book.Status = BookStatus.Canceled;
            book.CompletedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            cancelRegistry.Cancel(id); // best-effort: no-op if not running here
            await logger.LogAsync(book.Id, "info", "Cancellation requested.");
            await logger.UpdateStatusAsync(book.Id, BookStatus.Canceled);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{id:guid}/resume", async (Guid id, HttpContext http, AppDbContext db, BookEventLogger logger) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            if (book.Status != BookStatus.Paused) return Results.BadRequest(new { error = "Only paused books can be resumed." });
            book.Status = BookStatus.Running;
            await db.SaveChangesAsync();
            await logger.LogAsync(book.Id, "info", "Resumed.");
            await logger.UpdateStatusAsync(book.Id, BookStatus.Running);
            return Results.Ok(new { ok = true });
        });

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, AppDbContext db, BookEditorRepo editorRepo) =>
        {
            var book = await GetOwnedBookAsync(db, http, id);
            if (book is null) return Results.NotFound();
            if (File.Exists(book.InputStoragePath)) File.Delete(book.InputStoragePath);
            if (book.OutputStoragePath is not null && File.Exists(book.OutputStoragePath))
                File.Delete(book.OutputStoragePath);
            // Editor repo lives outside the upload/output paths under the
            // dedicated repo directory — clean it up too so the books
            // folder doesn't grow stale per-book repos forever.
            var repoPath = editorRepo.PathFor(id);
            if (Directory.Exists(repoPath))
            {
                try { ForceDeleteDirectory(repoPath); }
                catch { /* best-effort: leaks a directory but doesn't break delete */ }
            }
            db.Books.Remove(book);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static void ForceDeleteDirectory(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Collapse the stored <see cref="BookStatus"/> onto the simpler set the
    /// UI actually shows. The pill only ever needs to answer "is there
    /// something for me to review?" — Completed/Failed/Canceled aren't
    /// surfaced because the log panel already tells the user what happened.
    /// </summary>
    private static BookStatus DeriveDisplayStatus(Book book, BookEditorRepo editorRepo)
        => DeriveListDisplayStatus(book.Status, book.RepoPath, editorRepo);

    private static BookStatus DeriveListDisplayStatus(BookStatus raw, string? repoPath, BookEditorRepo editorRepo)
    {
        // Active states stay raw — the user wants to see live progress.
        if (raw is BookStatus.Queued or BookStatus.Running or BookStatus.Paused)
            return raw;
        // Everything else folds into a single bit: are there uncommitted
        // changes to review? AwaitingReview if yes, Idle if no.
        if (!string.IsNullOrEmpty(repoPath) && editorRepo.HasUncommittedChanges(repoPath))
            return BookStatus.AwaitingReview;
        return BookStatus.Idle;
    }

    public sealed record PromptDto(string? SystemPrompt);
    public sealed record MetadataDto(
        string? Title,
        string? Author,
        string? Language,
        string? Publisher,
        string? Description);

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
}

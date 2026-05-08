using System.Security.Claims;
using System.Text.Json;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

/// <summary>
/// Novel-level actions that aren't tied to a specific page (export, rerun
/// the AI pass, clone-and-reprocess). Per-page editor surface lives on
/// <see cref="PagesEndpoints"/>; the older review-list surface
/// (<c>/api/novels/{id}/reviews/*</c>) was removed when the editor switched
/// to the git-backed model.
/// </summary>
public static class NovelActionsEndpoints
{
    public static void MapNovelActionsEndpoints(this IEndpointRouteBuilder app)
    {
        // Apply the editor's HEAD to the original EPUB and write the cleaned
        // output. Mirrors what the auto-mode pass produces, but driven by the
        // user's explicit accept of the current review state.
        app.MapPost("/api/novels/{novelId:guid}/finalize", async (
            Guid novelId, HttpContext http, AppDbContext db, NovelFinalizer finalizer,
            CancellationToken ct) =>
        {
            if (await GetOwnedNovelAsync(db, http, novelId) is null) return Results.NotFound();
            var ok = await finalizer.FinalizeAsync(novelId, ct);
            if (!ok) return Results.BadRequest(new { error = "Novel is not awaiting review or has no editor repo." });
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Run the LLM pass against this novel, optionally restricted to a
        // subset of page paths. Replaces the older "rerun-ai" endpoint —
        // handles both fresh runs (Idle novels that have never been
        // processed) and reruns (Completed/AwaitingReview/Failed/Canceled).
        //
        // For reruns, fresh suggestions land in the editor's working tree on
        // top of the user's already-committed edits and surface as a diff
        // vs HEAD; for fresh runs, the worker behaves exactly as it would on
        // first upload (was previously the auto-enqueue path).
        //
        // Body: <c>{ "pages": ["pages/0001_foo.txt", ...] }</c> — optional;
        // when omitted or empty, the run covers the whole novel.
        app.MapPost("/api/novels/{novelId:guid}/run-ai", async (
            Guid novelId, HttpContext http, AppDbContext db, NovelProcessingQueue queue,
            [FromBody] RunAiDto? dto, CancellationToken ct) =>
        {
            var novel = await GetOwnedNovelAsync(db, http, novelId);
            if (novel is null) return Results.NotFound();
            if (novel.Status is NovelStatus.Running or NovelStatus.Queued or NovelStatus.Paused)
                return Results.BadRequest(new { error = "Novel is already running. Wait for it to finish." });
            if (!File.Exists(novel.InputStoragePath))
                return Results.BadRequest(new { error = "Original file is missing." });

            // Honor the admin master-switch — refuse to enqueue when the
            // AI feature is turned off, even if the user crafts the POST
            // by hand.
            var aiEnabled = await db.AppSettings.AsNoTracking()
                .Where(s => s.Id == AppSettings.SingletonKey)
                .Select(s => s.AiEnabled)
                .FirstOrDefaultAsync(ct);
            if (!aiEnabled)
                return Results.BadRequest(new { error = "AI features have been disabled by an admin." });

            // RerunRequested triggers the worker's "preserve existing review
            // state" path — only correct when the novel has been processed
            // before. A fresh Idle novel follows the standard first-run flow.
            novel.RerunRequested = novel.Status is not NovelStatus.Idle;
            novel.PagesFilterJson = dto?.Pages is { Count: > 0 }
                ? JsonSerializer.Serialize(dto.Pages)
                : null;
            novel.Status = NovelStatus.Queued;
            novel.ErrorMessage = null;
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(novel.Id, ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Clone a completed novel — copy its cleaned output into a fresh
        // novel entry that the user can run the AI on (or further edit)
        // independently. Lands in Idle status; the user explicitly clicks
        // Run AI on the new entry when ready, same as any fresh upload.
        app.MapPost("/api/novels/{novelId:guid}/clone", async (
            Guid novelId, HttpContext http, AppDbContext db, NovelImporter importer,
            CancellationToken ct) =>
        {
            var novel = await GetOwnedNovelAsync(db, http, novelId);
            if (novel is null) return Results.NotFound();
            if (novel.OutputStoragePath is null || !File.Exists(novel.OutputStoragePath))
                return Results.BadRequest(new { error = "Novel has no cleaned output to clone." });

            var appS = await db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey, ct);
            if (appS is null || string.IsNullOrWhiteSpace(appS.Model))
                return Results.BadRequest(new { error = "Global LLM settings not configured." });

            // Copy the output to a fresh upload-area path so the clone is
            // independent of the source novel's storage lifecycle.
            var newId = Guid.NewGuid();
            var uploadDir = Path.GetDirectoryName(novel.InputStoragePath)!;
            Directory.CreateDirectory(uploadDir);
            var newInput = Path.Combine(uploadDir, $"{newId:N}_{Path.GetFileName(novel.OriginalFileName)}");
            File.Copy(novel.OutputStoragePath, newInput);

            // Prime the editor repo + read metadata, same as a normal upload.
            var (repoPath, meta) = await importer.ImportAsync(newId, newInput, ct);

            var clone = new Novel
            {
                Id = newId,
                UserId = novel.UserId,
                OriginalFileName = novel.OriginalFileName,
                FileSizeBytes = new FileInfo(newInput).Length,
                InputStoragePath = newInput,
                RepoPath = repoPath,
                Title       = meta.Title,
                Author      = meta.Author,
                Language    = meta.Language,
                Publisher   = meta.Publisher,
                Description = meta.Description,
                Status = NovelStatus.Idle,
                MaxWorkers = appS.MaxWorkers,
                Model = appS.Model,
            };
            db.Novels.Add(clone);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = clone.Id });
        }).RequireAuthorization();
    }

    private static Guid GetUserId(HttpContext http)
        => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<Novel?> GetOwnedNovelAsync(AppDbContext db, HttpContext http, Guid id)
    {
        var userId = GetUserId(http);
        var isAdmin = http.User.IsInRole(AppRoles.Admin);
        return isAdmin
            ? await db.Novels.FirstOrDefaultAsync(n => n.Id == id)
            : await db.Novels.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
    }

    public sealed record RunAiDto(List<string>? Pages);
}

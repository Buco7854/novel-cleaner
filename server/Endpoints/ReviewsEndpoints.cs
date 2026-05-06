using System.Security.Claims;
using System.Text.Json;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

/// <summary>
/// Job-level actions that are not tied to a specific page (export, rerun the
/// AI pass, clone-and-reprocess). Per-page editor surface lives on
/// <see cref="PagesEndpoints"/>; the older review-list surface
/// (<c>/api/novels/{id}/reviews/*</c>) was removed when the editor switched to
/// the git-backed model.
/// </summary>
public static class ReviewsEndpoints
{
    public static void MapReviewsEndpoints(this IEndpointRouteBuilder app)
    {
        // Apply the editor's HEAD to the original EPUB and write the cleaned
        // output. Mirrors what the auto-mode pass produces, but driven by the
        // user's explicit accept of the current review state.
        app.MapPost("/api/novels/{jobId:guid}/finalize", async (
            Guid jobId, HttpContext http, AppDbContext db, JobFinalizer finalizer,
            CancellationToken ct) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            var ok = await finalizer.FinalizeAsync(jobId, ct);
            if (!ok) return Results.BadRequest(new { error = "Job is not awaiting review or has no editor repo." });
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Run the LLM pass against this job, optionally restricted to a
        // subset of page paths. Replaces the older "rerun-ai" endpoint —
        // handles both fresh runs (Idle jobs that have never been processed)
        // and reruns (Completed/AwaitingReview/Failed/Canceled jobs).
        //
        // For reruns, fresh suggestions land in the editor's working tree on
        // top of the user's already-committed edits and surface as a diff
        // vs HEAD; for fresh runs, the worker behaves exactly as it would on
        // first upload (was previously the auto-enqueue path).
        //
        // Body: <c>{ "pages": ["pages/0001_foo.txt", ...] }</c> — optional;
        // when omitted or empty, the run covers the whole book.
        app.MapPost("/api/novels/{jobId:guid}/run-ai", async (
            Guid jobId, HttpContext http, AppDbContext db, JobQueue queue,
            [FromBody] RunAiDto? dto, CancellationToken ct) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (job.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Paused)
                return Results.BadRequest(new { error = "Job is already running. Wait for it to finish." });
            if (!File.Exists(job.InputStoragePath))
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
            // state" path — only correct when the job has been processed
            // before. A fresh Idle job follows the standard first-run flow.
            job.RerunRequested = job.Status is not JobStatus.Idle;
            job.PagesFilterJson = dto?.Pages is { Count: > 0 }
                ? JsonSerializer.Serialize(dto.Pages)
                : null;
            job.Status = JobStatus.Queued;
            job.ErrorMessage = null;
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(job.Id, ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Clone a completed book — copy its cleaned output into a fresh
        // book entry that the user can run the AI on (or further edit)
        // independently. Lands in Idle status; the user explicitly clicks
        // Run AI on the new book when ready, same as any fresh upload.
        app.MapPost("/api/novels/{jobId:guid}/clone", async (
            Guid jobId, HttpContext http, AppDbContext db, BookImporter importer,
            CancellationToken ct) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (job.OutputStoragePath is null || !File.Exists(job.OutputStoragePath))
                return Results.BadRequest(new { error = "Book has no cleaned output to clone." });

            var appS = await db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey, ct);
            if (appS is null || string.IsNullOrWhiteSpace(appS.Model))
                return Results.BadRequest(new { error = "Global LLM settings not configured." });

            // Copy the output to a fresh upload-area path so the clone is
            // independent of the source book's storage lifecycle.
            var newId = Guid.NewGuid();
            var uploadDir = Path.GetDirectoryName(job.InputStoragePath)!;
            Directory.CreateDirectory(uploadDir);
            var newInput = Path.Combine(uploadDir, $"{newId:N}_{Path.GetFileName(job.OriginalFileName)}");
            File.Copy(job.OutputStoragePath, newInput);

            // Prime the editor repo + read metadata, same as a normal upload.
            var (repoPath, meta) = await importer.ImportAsync(newId, newInput, ct);

            var clone = new CleanJob
            {
                Id = newId,
                UserId = job.UserId,
                OriginalFileName = job.OriginalFileName,
                FileSizeBytes = new FileInfo(newInput).Length,
                InputStoragePath = newInput,
                RepoPath = repoPath,
                Title       = meta.Title,
                Author      = meta.Author,
                Language    = meta.Language,
                Publisher   = meta.Publisher,
                Description = meta.Description,
                Status = JobStatus.Idle,
                MaxWorkers = appS.MaxWorkers,
                Model = appS.Model,
            };
            db.CleanJobs.Add(clone);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = clone.Id });
        }).RequireAuthorization();
    }

    private static Guid GetUserId(HttpContext http)
        => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<CleanJob?> GetOwnedJobAsync(AppDbContext db, HttpContext http, Guid id)
    {
        var userId = GetUserId(http);
        var isAdmin = http.User.IsInRole(AppRoles.Admin);
        return isAdmin
            ? await db.CleanJobs.FirstOrDefaultAsync(j => j.Id == id)
            : await db.CleanJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
    }

    public sealed record RunAiDto(List<string>? Pages);
}

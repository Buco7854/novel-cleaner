using System.Security.Claims;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

/// <summary>
/// Job-level actions that are not tied to a specific page (export, rerun the
/// AI pass, clone-and-reprocess). Per-page editor surface lives on
/// <see cref="PagesEndpoints"/>; the older review-list surface
/// (<c>/api/jobs/{id}/reviews/*</c>) was removed when the editor switched to
/// the git-backed model.
/// </summary>
public static class ReviewsEndpoints
{
    public static void MapReviewsEndpoints(this IEndpointRouteBuilder app)
    {
        // Apply the editor's HEAD to the original EPUB and write the cleaned
        // output. Mirrors what the auto-mode pass produces, but driven by the
        // user's explicit accept of the current review state.
        app.MapPost("/api/jobs/{jobId:guid}/finalize", async (
            Guid jobId, HttpContext http, AppDbContext db, JobFinalizer finalizer,
            CancellationToken ct) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            var ok = await finalizer.FinalizeAsync(jobId, ct);
            if (!ok) return Results.BadRequest(new { error = "Job is not awaiting review or has no editor repo." });
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Re-run the LLM pass against this same job. New suggestions land in
        // the editor's working tree on top of the user's already-committed
        // edits — they're surfaced as a fresh diff vs HEAD so the user sees
        // exactly what the model just proposed.
        app.MapPost("/api/jobs/{jobId:guid}/rerun-ai", async (
            Guid jobId, HttpContext http, AppDbContext db, JobQueue queue,
            CancellationToken ct) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            // Block reruns for in-flight states; everything terminal-ish is OK.
            if (job.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Paused)
                return Results.BadRequest(new { error = "Job is already running. Wait for it to finish." });
            if (!File.Exists(job.InputStoragePath))
                return Results.BadRequest(new { error = "Original file is missing." });

            job.RerunRequested = true;
            job.Status = JobStatus.Queued;
            job.ErrorMessage = null;
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(job.Id, ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Reprocess: clone a Completed job's output as a new input. Inherits
        // the user's *current* settings (including ReviewBeforeApplying).
        app.MapPost("/api/jobs/{jobId:guid}/reprocess", async (
            Guid jobId, HttpContext http, AppDbContext db, JobQueue queue,
            CancellationToken ct) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (job.OutputStoragePath is null || !File.Exists(job.OutputStoragePath))
                return Results.BadRequest(new { error = "Job has no output file to reprocess." });

            var userS = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == job.UserId, ct);
            var appS = await db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey, ct);
            if (appS is null || string.IsNullOrWhiteSpace(appS.Model))
                return Results.BadRequest(new { error = "Global LLM settings not configured." });
            if (userS is null) userS = new UserSettings { UserId = job.UserId };

            // Copy the output file to a fresh upload-area path so it's
            // independent of the original job's lifecycle.
            var newId = Guid.NewGuid();
            var uploadDir = Path.GetDirectoryName(job.InputStoragePath)!;
            Directory.CreateDirectory(uploadDir);
            var newInput = Path.Combine(uploadDir, $"{newId:N}_{Path.GetFileName(job.OriginalFileName)}");
            File.Copy(job.OutputStoragePath, newInput);

            var clone = new CleanJob
            {
                Id = newId,
                UserId = job.UserId,
                OriginalFileName = job.OriginalFileName,
                FileSizeBytes = new FileInfo(newInput).Length,
                InputStoragePath = newInput,
                ScanAll = userS.ScanAll,
                PatternsJson = userS.PatternsJson,
                ContextWindow = userS.ContextWindow,
                ReviewBeforeApplying = userS.ReviewBeforeApplying,
                MaxWorkers = appS.MaxWorkers,
                Model = appS.Model,
            };
            db.CleanJobs.Add(clone);
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(clone.Id, ct);
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
}

using System.Security.Claims;
using System.Text.Json;
using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, AppDbContext db,
            [FromQuery] int page = 1, [FromQuery] int size = 20) =>
        {
            size = Math.Clamp(size, 1, 100);
            page = Math.Max(1, page);
            var userId = GetUserId(http);
            var q = db.CleanJobs.AsNoTracking().Where(j => j.UserId == userId);
            var total = await q.CountAsync();
            var items = await q.OrderByDescending(j => j.CreatedAt)
                .Skip((page - 1) * size)
                .Take(size)
                .Select(j => new
                {
                    id = j.Id,
                    fileName = j.OriginalFileName,
                    sizeBytes = j.FileSizeBytes,
                    status = j.Status.ToString(),
                    scanAll = j.ScanAll,
                    model = j.Model,
                    removed = j.RemovedCount,
                    createdAt = j.CreatedAt,
                    completedAt = j.CompletedAt,
                    hasOutput = j.OutputStoragePath != null,
                }).ToListAsync();
            return Results.Ok(new { items, total, page, size });
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext http,
            AppDbContext db,
            UserManager<AppUser> userManager,
            IOptionsMonitor<AuthOptions> authOpts) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            var logs = await db.JobLogs.AsNoTracking()
                .Where(l => l.JobId == id).OrderBy(l => l.Id).Take(2000)
                .Select(l => new { l.Timestamp, l.Level, l.Message, l.Detail, l.GroupId })
                .ToListAsync();
            var patternCount = 0;
            try { patternCount = JsonSerializer.Deserialize<List<string>>(job.PatternsJson)?.Count ?? 0; }
            catch { /* leave at 0 */ }

            var appSettings = await db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey);
            var dropConfigured = !string.IsNullOrWhiteSpace(appSettings?.DropFolder);

            // canDrop = drop folder is configured AND the job's owner has the
            // BookDrop permission. The currently-signed-in user (might be an
            // admin viewing someone else's job) doesn't gate the button.
            var canDrop = false;
            if (dropConfigured)
            {
                var owner = await userManager.FindByIdAsync(job.UserId.ToString());
                if (owner is not null)
                    canDrop = await Permissions.CanUseDropFolderAsync(userManager, owner, authOpts.CurrentValue);
            }

            return Results.Ok(new
            {
                id = job.Id,
                fileName = job.OriginalFileName,
                status = job.Status.ToString(),
                model = job.Model,
                scanAll = job.ScanAll,
                patternCount,
                contextWindow = job.ContextWindow,
                maxWorkers = job.MaxWorkers,
                removed = job.RemovedCount,
                error = job.ErrorMessage,
                createdAt = job.CreatedAt,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                hasOutput = job.OutputStoragePath != null,
                canDrop,
                logs,
            });
        });

        group.MapPost("/{id:guid}/drop", async (
            Guid id,
            HttpContext http,
            AppDbContext db,
            JobLogger logger,
            UserManager<AppUser> userManager,
            IOptionsMonitor<AuthOptions> authOpts) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            if (job.OutputStoragePath is null || !File.Exists(job.OutputStoragePath))
                return Results.BadRequest(new { error = "Job has no output file." });

            var appSettings = await db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettings.SingletonKey);
            if (appSettings is null || string.IsNullOrWhiteSpace(appSettings.DropFolder))
                return Results.BadRequest(new { error = "No drop folder configured in settings." });

            // Permission is keyed to the JOB OWNER, not the caller — admins
            // viewing another user's job shouldn't be able to push into the
            // drop folder on behalf of a non-permitted user.
            var owner = await userManager.FindByIdAsync(job.UserId.ToString());
            if (owner is null
                || !await Permissions.CanUseDropFolderAsync(userManager, owner, authOpts.CurrentValue))
                return Results.Forbid();

            try
            {
                var result = DropFolderHelper.Copy(appSettings.DropFolder, job.OriginalFileName, job.OutputStoragePath);
                await logger.LogAsync(job.Id, "info", $"Manually copied to drop folder: {result.DestinationPath}");
                return Results.Ok(new { destination = result.DestinationPath });
            }
            catch (Exception ex)
            {
                await logger.LogAsync(job.Id, "warn", $"Manual drop folder copy failed: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        group.MapGet("/{id:guid}/download", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null || job.OutputStoragePath is null || !File.Exists(job.OutputStoragePath))
                return Results.NotFound();
            var stem = Path.GetFileNameWithoutExtension(job.OriginalFileName);
            return Results.File(job.OutputStoragePath, "application/epub+zip", $"{stem}_cleaned.epub");
        });

        group.MapPost("/", async (
            HttpContext http,
            AppDbContext db,
            JobQueue queue,
            IOptions<StorageOptions> storage) =>
        {
            // Multipart form posts are "simple" CORS requests, so a malicious
            // cross-origin page can submit one with the user's cookie.
            // Require a non-simple custom header so any cross-origin request
            // triggers a CORS preflight, which we don't allow.
            if (!http.Request.Headers.TryGetValue("X-Requested-With", out _))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var userId = GetUserId(http);
            var appS = await db.AppSettings.FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey);
            if (appS is null || string.IsNullOrWhiteSpace(appS.Model))
                return Results.BadRequest(new { error = "Global LLM settings have not been configured by an admin yet." });
            var userS = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId);
            if (userS is null)
            {
                userS = new UserSettings { UserId = userId };
                db.UserSettings.Add(userS);
                await db.SaveChangesAsync();
            }

            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await http.Request.ReadFormAsync();
            var files = form.Files.Where(f => f.Length > 0).ToList();
            if (files.Count == 0) return Results.BadRequest(new { error = "No files uploaded" });

            var scanAllOverride = bool.TryParse(form["scanAll"], out var sa) ? sa : (bool?)null;
            var patternsOverride = form["patterns"].ToString();

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

                var job = new CleanJob
                {
                    Id = id,
                    UserId = userId,
                    OriginalFileName = safeName,
                    FileSizeBytes = file.Length,
                    InputStoragePath = inputPath,
                    // Per-user (UserSettings)
                    ScanAll = scanAllOverride ?? userS.ScanAll,
                    PatternsJson = string.IsNullOrWhiteSpace(patternsOverride)
                        ? userS.PatternsJson
                        : patternsOverride,
                    ContextWindow = userS.ContextWindow,
                    // Admin-managed (AppSettings)
                    MaxWorkers = appS.MaxWorkers,
                    Model = appS.Model,
                };
                db.CleanJobs.Add(job);
                created.Add(id);
            }
            await db.SaveChangesAsync();
            foreach (var id in created) await queue.EnqueueAsync(id);
            return Results.Ok(new { ids = created });
        }).DisableAntiforgery();

        group.MapPost("/{id:guid}/pause", async (Guid id, HttpContext http, AppDbContext db, JobLogger logger) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            if (job.Status != JobStatus.Running) return Results.BadRequest(new { error = "Only running jobs can be paused." });
            job.Status = JobStatus.Paused;
            await db.SaveChangesAsync();
            await logger.LogAsync(job.Id, "info", "Pause requested — no new excerpts will start until resumed.");
            await logger.UpdateStatusAsync(job.Id, JobStatus.Paused);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{id:guid}/resume", async (Guid id, HttpContext http, AppDbContext db, JobLogger logger) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            if (job.Status != JobStatus.Paused) return Results.BadRequest(new { error = "Only paused jobs can be resumed." });
            job.Status = JobStatus.Running;
            await db.SaveChangesAsync();
            await logger.LogAsync(job.Id, "info", "Resumed.");
            await logger.UpdateStatusAsync(job.Id, JobStatus.Running);
            return Results.Ok(new { ok = true });
        });

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            if (File.Exists(job.InputStoragePath)) File.Delete(job.InputStoragePath);
            if (job.OutputStoragePath is not null && File.Exists(job.OutputStoragePath))
                File.Delete(job.OutputStoragePath);
            db.CleanJobs.Remove(job);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
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

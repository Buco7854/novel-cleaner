using System.Security.Claims;
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
        var group = app.MapGroup("/api/novels").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, AppDbContext db, BookRepo bookRepo,
            [FromQuery] int page = 1, [FromQuery] int size = 20) =>
        {
            size = Math.Clamp(size, 1, 100);
            page = Math.Max(1, page);
            var userId = GetUserId(http);
            var q = db.CleanJobs.AsNoTracking().Where(j => j.UserId == userId);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(j => j.CreatedAt)
                .Skip((page - 1) * size)
                .Take(size)
                .Select(j => new
                {
                    j.Id, j.OriginalFileName, j.Title, j.Author,
                    j.FileSizeBytes, j.Status, j.Model, j.RemovedCount,
                    j.CreatedAt, j.CompletedAt, j.OutputStoragePath, j.RepoPath,
                }).ToListAsync();

            // Derive the same simplified status as the detail endpoint —
            // cheap libgit2 status call, runs at most 100 times per page.
            var items = rows.Select(j =>
            {
                var status = DeriveListDisplayStatus(j.Status, j.RepoPath, bookRepo);
                return new
                {
                    id = j.Id,
                    fileName = j.OriginalFileName,
                    title = j.Title,
                    author = j.Author,
                    sizeBytes = j.FileSizeBytes,
                    status = status.ToString(),
                    model = j.Model,
                    removed = j.RemovedCount,
                    createdAt = j.CreatedAt,
                    completedAt = j.CompletedAt,
                    hasOutput = j.OutputStoragePath != null,
                };
            }).ToList();
            return Results.Ok(new { items, total, page, size });
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext http,
            AppDbContext db,
            BookRepo bookRepo) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            var logs = await db.JobLogs.AsNoTracking()
                .Where(l => l.JobId == id).OrderBy(l => l.Id).Take(2000)
                .Select(l => new { l.Timestamp, l.Level, l.Message, l.Detail, l.GroupId })
                .ToListAsync();

            var appSettings = await db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey);
            var dropConfigured = !string.IsNullOrWhiteSpace(appSettings?.DropFolder);

            // dropConfigured is what the editor's "Push to folder" button now
            // gates on: as soon as an admin sets a drop folder, the button
            // shows. The actual permission check still runs server-side at
            // POST /drop time and returns 403 if the user lacks BookDrop —
            // that error surfaces as a toast, which is a clearer UX than
            // silently hiding the button.

            // The user-facing status set is intentionally tiny:
            //   Queued / Running / Paused  ← active worker states (raw)
            //   AwaitingReview             ← repo has uncommitted changes
            //   Idle                       ← everything else
            // Completed / Failed / Canceled aren't surfaced — the log panel
            // tells you what happened, the pill just answers "is there
            // anything for me to review?".
            var status = DeriveDisplayStatus(job, bookRepo);

            return Results.Ok(new
            {
                id = job.Id,
                fileName = job.OriginalFileName,
                title       = job.Title,
                author      = job.Author,
                language    = job.Language,
                publisher   = job.Publisher,
                description = job.Description,
                status = status.ToString(),
                model = job.Model,
                maxWorkers = job.MaxWorkers,
                removed = job.RemovedCount,
                error = job.ErrorMessage,
                createdAt = job.CreatedAt,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                hasOutput = job.OutputStoragePath != null,
                dropConfigured,
                systemPrompt = job.SystemPrompt ?? "",
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
            BookImporter importer,
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

                var job = new CleanJob
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
                    Status = JobStatus.Idle,
                    // Admin-managed (AppSettings)
                    MaxWorkers = appS.MaxWorkers,
                    Model = appS.Model,
                };
                db.CleanJobs.Add(job);
                created.Add(id);
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { ids = created });
        }).DisableAntiforgery();

        // Update the per-novel AI instructions. Saves to the row's
        // SystemPrompt column; the worker layers this on top of the admin
        // and user prompts at LLM call time.
        group.MapPut("/{id:guid}/prompt", async (
            Guid id, HttpContext http, AppDbContext db, [FromBody] PromptDto dto) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            job.SystemPrompt = string.IsNullOrWhiteSpace(dto.SystemPrompt)
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
            Guid id, HttpContext http, AppDbContext db, JobLogger logger, [FromBody] MetadataDto dto) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();

            string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            job.Title       = Norm(dto.Title);
            job.Author      = Norm(dto.Author);
            job.Language    = Norm(dto.Language);
            job.Publisher   = Norm(dto.Publisher);
            job.Description = Norm(dto.Description);
            await db.SaveChangesAsync();

            // Write to BOTH the input and the output EPUB. The download
            // endpoint serves OutputStoragePath after a finalize — without
            // this second write a metadata edit made post-finalize would
            // appear in the editor's title but not in the file the user
            // actually downloads.
            var meta = new EpubMetadata(
                job.Title, job.Author, job.Language, job.Publisher, job.Description);
            foreach (var path in new[] { job.InputStoragePath, job.OutputStoragePath })
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                try { EpubHandler.WriteMetadata(path, meta); }
                catch (Exception ex)
                {
                    await logger.LogAsync(job.Id, "warn",
                        $"Could not write metadata into {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            return Results.Ok(new
            {
                title       = job.Title,
                author      = job.Author,
                language    = job.Language,
                publisher   = job.Publisher,
                description = job.Description,
            });
        });

        // Wipe the persisted log lines for a job. The editor's "Clear" button
        // calls this so a refresh doesn't repopulate the panel from server
        // history. Live SignalR feed continues for any new lines emitted by
        // an in-flight run.
        group.MapDelete("/{id:guid}/logs", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            await db.JobLogs.Where(l => l.JobId == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

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

        // Cancel a Queued/Running/Paused run. Two paths converge here:
        // - Queued: nothing is running yet, so we just flip the DB to
        //   Canceled. The worker checks this before starting.
        // - Running/Paused: same DB flip, plus we signal the registered
        //   CTS so the in-flight LLM calls actually interrupt. Without
        //   the signal a Running job would keep firing requests until
        //   the chapter loop naturally exits.
        group.MapPost("/{id:guid}/cancel", async (
            Guid id, HttpContext http, AppDbContext db, JobLogger logger,
            JobCancellationRegistry cancelRegistry) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            if (job.Status is not (JobStatus.Queued or JobStatus.Running or JobStatus.Paused))
                return Results.BadRequest(new { error = "Only queued, running, or paused jobs can be canceled." });

            job.Status = JobStatus.Canceled;
            job.CompletedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            cancelRegistry.Cancel(id); // best-effort: no-op if not running here
            await logger.LogAsync(job.Id, "info", "Cancellation requested.");
            await logger.UpdateStatusAsync(job.Id, JobStatus.Canceled);
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

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, AppDbContext db, BookRepo repos) =>
        {
            var job = await GetOwnedJobAsync(db, http, id);
            if (job is null) return Results.NotFound();
            if (File.Exists(job.InputStoragePath)) File.Delete(job.InputStoragePath);
            if (job.OutputStoragePath is not null && File.Exists(job.OutputStoragePath))
                File.Delete(job.OutputStoragePath);
            // Editor repo lives outside the upload/output paths under the
            // dedicated repo directory — clean it up too so the books
            // folder doesn't grow stale per-job repos forever.
            var repoPath = repos.PathFor(id);
            if (Directory.Exists(repoPath))
            {
                try { ForceDeleteDirectory(repoPath); }
                catch { /* best-effort: leaks a directory but doesn't break delete */ }
            }
            db.CleanJobs.Remove(job);
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
    /// Collapse the stored CleanJob.Status onto the simpler set the UI
    /// actually shows. The pill only ever needs to answer "is there something
    /// for me to review?" — Completed/Failed/Canceled aren't surfaced because
    /// the log panel already tells the user what happened.
    /// </summary>
    private static JobStatus DeriveDisplayStatus(CleanJob job, BookRepo bookRepo)
        => DeriveListDisplayStatus(job.Status, job.RepoPath, bookRepo);

    private static JobStatus DeriveListDisplayStatus(JobStatus raw, string? repoPath, BookRepo bookRepo)
    {
        // Active states stay raw — the user wants to see live progress.
        if (raw is JobStatus.Queued or JobStatus.Running or JobStatus.Paused)
            return raw;
        // Everything else folds into a single bit: are there uncommitted
        // changes to review? AwaitingReview if yes, Idle if no.
        if (!string.IsNullOrEmpty(repoPath) && bookRepo.HasUncommittedChanges(repoPath))
            return JobStatus.AwaitingReview;
        return JobStatus.Idle;
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

    private static async Task<CleanJob?> GetOwnedJobAsync(AppDbContext db, HttpContext http, Guid id)
    {
        var userId = GetUserId(http);
        var isAdmin = http.User.IsInRole(AppRoles.Admin);
        return isAdmin
            ? await db.CleanJobs.FirstOrDefaultAsync(j => j.Id == id)
            : await db.CleanJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
    }
}

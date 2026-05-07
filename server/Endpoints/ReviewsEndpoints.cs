using System.Security.Claims;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

public static class ReviewsEndpoints
{
    public static void MapReviewsEndpoints(this IEndpointRouteBuilder app)
    {
        // Hung off /api/jobs because reviews are scoped to a job. The job's
        // owner (or an admin) is the only one allowed to interact.
        var group = app.MapGroup("/api/jobs/{jobId:guid}/reviews").RequireAuthorization();

        // List the chapters in a job's review with proposal counts. Cheap —
        // used to render the chapter sidebar.
        group.MapGet("/", async (Guid jobId, HttpContext http, AppDbContext db) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            var rows = await db.ChapterReviews.AsNoTracking()
                .Where(r => r.JobId == jobId)
                .OrderBy(r => r.OrderIndex)
                .Select(r => new
                {
                    documentName = r.DocumentName,
                    orderIndex = r.OrderIndex,
                    accepted = r.Proposals.Count(p => p.Decision == ProposalDecision.Accepted),
                    rejected = r.Proposals.Count(p => p.Decision == ProposalDecision.Rejected),
                    pending = r.Proposals.Count(p => p.Decision == ProposalDecision.Pending),
                    llmCount = r.Proposals.Count(p => p.Source == ProposalSource.Llm),
                    userCount = r.Proposals.Count(p => p.Source == ProposalSource.User),
                })
                .ToListAsync();
            return Results.Ok(rows);
        });

        // One chapter's full review state — visible text + proposals.
        // docName url-encoded as a query param because zip entries contain '/'.
        group.MapGet("/chapter", async (
            Guid jobId, HttpContext http, AppDbContext db,
            [FromQuery] string doc) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            var review = await db.ChapterReviews
                .Include(r => r.Proposals)
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.JobId == jobId && r.DocumentName == doc);
            if (review is null) return Results.NotFound();
            return Results.Ok(new
            {
                documentName = review.DocumentName,
                visibleText = review.VisibleText,
                proposals = review.Proposals
                    .OrderBy(p => p.Source).ThenBy(p => p.Id)
                    .Select(p => new
                    {
                        id = p.Id,
                        text = p.Text,
                        reason = p.Reason,
                        source = p.Source.ToString(),
                        decision = p.Decision.ToString(),
                    }),
            });
        });

        // Accept/reject one proposal. The {proposalId} is a long because it's
        // the EF identity column — Guid would be overkill on a per-job table.
        group.MapPost("/decisions/{proposalId:long}", async (
            Guid jobId, long proposalId, HttpContext http, AppDbContext db,
            [FromBody] DecisionDto dto) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            if (!Enum.TryParse<ProposalDecision>(dto.Decision, ignoreCase: true, out var decision))
                return Results.BadRequest(new { error = "decision must be Pending, Accepted, or Rejected" });

            var proposal = await db.ReviewProposals
                .Include(p => p.Chapter)
                .FirstOrDefaultAsync(p => p.Id == proposalId && p.Chapter.JobId == jobId);
            if (proposal is null) return Results.NotFound();

            proposal.Decision = decision;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Add a user-authored proposal — defaults to Source=User, Decision=Accepted.
        group.MapPost("/proposals", async (
            Guid jobId, HttpContext http, AppDbContext db,
            [FromQuery] string doc,
            [FromBody] AddProposalDto dto) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.BadRequest(new { error = "text required" });

            var review = await db.ChapterReviews
                .FirstOrDefaultAsync(r => r.JobId == jobId && r.DocumentName == doc);
            if (review is null) return Results.NotFound();

            var p = new ReviewProposal
            {
                ChapterReviewId = review.Id,
                Text = dto.Text.Trim(),
                Reason = (dto.Reason ?? "User-added removal").Trim(),
                Source = ProposalSource.User,
                Decision = ProposalDecision.Accepted,
            };
            db.ReviewProposals.Add(p);
            await db.SaveChangesAsync();
            return Results.Ok(new { id = p.Id });
        });

        // Delete a user-authored proposal. LLM proposals can only be rejected,
        // never deleted — keeps an audit trail of what the LLM suggested.
        group.MapDelete("/proposals/{proposalId:long}", async (
            Guid jobId, long proposalId, HttpContext http, AppDbContext db) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            var proposal = await db.ReviewProposals
                .Include(p => p.Chapter)
                .FirstOrDefaultAsync(p => p.Id == proposalId && p.Chapter.JobId == jobId);
            if (proposal is null) return Results.NotFound();
            if (proposal.Source != ProposalSource.User)
                return Results.BadRequest(new { error = "Only user-authored proposals can be deleted; reject LLM proposals instead." });
            db.ReviewProposals.Remove(proposal);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Re-prompt the LLM for a chapter (or a selected text range within
        // the chapter's visible text). New items are appended as Pending so
        // the user explicitly sees what was just proposed.
        group.MapPost("/reprompt", async (
            Guid jobId, HttpContext http, AppDbContext db, OpenAiClient openai,
            JobLogger logger,
            [FromQuery] string doc,
            [FromBody] RepromptDto dto, CancellationToken ct) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (job.Status != JobStatus.AwaitingReview)
                return Results.BadRequest(new { error = "Job is not awaiting review." });

            var review = await db.ChapterReviews
                .FirstOrDefaultAsync(r => r.JobId == jobId && r.DocumentName == doc, ct);
            if (review is null) return Results.NotFound();

            var appS = await db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey, ct);
            if (appS is null || string.IsNullOrWhiteSpace(appS.ApiKey) || string.IsNullOrWhiteSpace(job.Model))
                return Results.BadRequest(new { error = "LLM settings not configured." });

            // Slice the visible text if a range is supplied — clamped to the
            // string bounds so a stale UI offset can't blow up.
            var fullText = review.VisibleText;
            var start = Math.Clamp(dto.RangeStart ?? 0, 0, fullText.Length);
            var end = Math.Clamp(dto.RangeEnd ?? fullText.Length, start, fullText.Length);
            var slice = fullText[start..end];
            if (string.IsNullOrWhiteSpace(slice))
                return Results.BadRequest(new { error = "Selected range is empty." });

            var result = await openai.IdentifyWatermarksAsync(
                [slice], [], appS.ApiKey!, appS.BaseUrl, job.Model, ct, appS.SystemPrompt);
            var added = 0;
            foreach (var item in result.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Remove)) continue;
                db.ReviewProposals.Add(new ReviewProposal
                {
                    ChapterReviewId = review.Id,
                    Text = item.Remove.Trim(),
                    Reason = item.Reason ?? "Re-prompted",
                    Source = ProposalSource.Llm,
                    Decision = ProposalDecision.Pending,
                });
                added++;
            }
            await db.SaveChangesAsync(ct);
            await logger.LogAsync(jobId, "info",
                $"{doc}: re-prompt added {added} proposal(s)", ct,
                detail: result.RawText, groupId: doc);
            return Results.Ok(new { added });
        });

        // Apply accepted proposals, write the cleaned EPUB, mark Completed.
        // Sibling endpoint at /api/jobs/{id}/finalize (not under /reviews/) so
        // the URL reads naturally; map it on the parent group.
        app.MapPost("/api/jobs/{jobId:guid}/finalize", async (
            Guid jobId, HttpContext http, AppDbContext db, JobFinalizer finalizer,
            CancellationToken ct) =>
        {
            if (await GetOwnedJobAsync(db, http, jobId) is null) return Results.NotFound();
            var ok = await finalizer.FinalizeAsync(jobId, ct);
            if (!ok) return Results.BadRequest(new { error = "Job is not awaiting review." });
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Re-run the LLM identification pass against THIS file (no clone) —
        // existing accepted/rejected/user proposals stay; new LLM findings
        // are appended as Pending so the user explicitly sees them. Used
        // from the file editor's "Run AI" button.
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

    public sealed record DecisionDto(string Decision);
    public sealed record AddProposalDto(string Text, string? Reason);
    public sealed record RepromptDto(int? RangeStart, int? RangeEnd);
}

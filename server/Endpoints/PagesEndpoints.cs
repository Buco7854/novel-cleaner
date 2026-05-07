using System.Security.Claims;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

/// <summary>
/// File-editor surface — surfaces a job's git-backed page repository.
/// Each route is rooted at <c>/api/jobs/{jobId}</c> because pages belong to
/// a job (file). The page <c>path</c> contains '/' so it travels in a query
/// param rather than a route segment.
/// </summary>
public static class PagesEndpoints
{
    public static void MapPagesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs/{jobId:guid}/pages").RequireAuthorization();

        // Sidebar feed — list every page with a coarse status flag so the UI
        // can put a dot next to modified ones.
        group.MapGet("/", async (Guid jobId, HttpContext http, AppDbContext db, BookRepo repos) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (string.IsNullOrEmpty(job.RepoPath))
                return Results.Ok(Array.Empty<object>());
            var pages = repos.ListPages(job.RepoPath)
                .Select(p => new { path = p.Path, orderIndex = p.OrderIndex, status = p.Status.ToString() });
            return Results.Ok(pages);
        });

        // One page's working-tree content + (if dirty) unified diff vs HEAD.
        // The frontend renders content in the contenteditable; the diff
        // hunks drive the accept/reject UI.
        group.MapGet("/page", async (
            Guid jobId, HttpContext http, AppDbContext db, BookRepo repos,
            [FromQuery] string path) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (string.IsNullOrEmpty(job.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            try
            {
                var p = repos.ReadPage(job.RepoPath, path);
                return Results.Ok(new
                {
                    path = p.Path,
                    content = p.Content,
                    status = p.Status.ToString(),
                    diff = p.DiffAgainstHead,
                });
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });

        // Save the user's edit to the working tree. No commit — the diff
        // shows up immediately and the user explicitly accepts via /commit.
        group.MapPut("/page", async (
            Guid jobId, HttpContext http, AppDbContext db, BookRepo repos,
            [FromQuery] string path, [FromBody] WriteDto dto) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (string.IsNullOrEmpty(job.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            repos.WritePage(job.RepoPath, path, dto.Content ?? "");
            return Results.NoContent();
        });

        // Stage + commit everything in the working tree. Maps to the user's
        // "accept all changes" action; hunk-level accept lands in a follow-
        // up that diff-parses and stages partial files.
        group.MapPost("/commit", async (
            Guid jobId, HttpContext http, AppDbContext db, BookRepo repos,
            [FromBody] CommitDto dto) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (string.IsNullOrEmpty(job.RepoPath)) return Results.NotFound();
            var msg = string.IsNullOrWhiteSpace(dto.Message) ? "User edit" : dto.Message.Trim();
            var committed = repos.CommitAll(job.RepoPath, msg);
            return Results.Ok(new { committed });
        });

        // Throw away every working-tree change for one page (= reject all
        // proposals on that page). The committed history is untouched.
        group.MapPost("/discard", async (
            Guid jobId, HttpContext http, AppDbContext db, BookRepo repos,
            [FromQuery] string path) =>
        {
            var job = await GetOwnedJobAsync(db, http, jobId);
            if (job is null) return Results.NotFound();
            if (string.IsNullOrEmpty(job.RepoPath)) return Results.NotFound();
            if (!IsSafeRelPath(path)) return Results.BadRequest(new { error = "invalid path" });
            repos.DiscardPage(job.RepoPath, path);
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

    /// <summary>
    /// Fast-fail path-traversal guard. Pages live under <c>pages/</c> and
    /// must not contain <c>..</c>; stronger validation happens inside
    /// <see cref="BookRepo.WritePage"/> too.
    /// </summary>
    private static bool IsSafeRelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("..")) return false;
        return path.StartsWith("pages/", StringComparison.Ordinal);
    }

    public sealed record WriteDto(string? Content);
    public sealed record CommitDto(string? Message);
}

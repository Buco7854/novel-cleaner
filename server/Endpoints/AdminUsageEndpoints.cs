using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Tergeo.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Tergeo.Server.Endpoints;

/// <summary>
/// Admin-only operational view: every Book in an active state across all
/// users, with shortcuts to pause / resume / cancel them. The per-book
/// owner pause / resume / cancel endpoints in <see cref="BooksEndpoints"/>
/// already accept admin callers acting on someone else's row, so the
/// admin page just calls those URLs directly — this file only adds the
/// list endpoint.
/// </summary>
public static class AdminUsageEndpoints
{
    public static void MapAdminUsageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/usage")
            .RequireAuthorization(p => p.RequireRole(AppRoles.Admin));

        // Snapshot of every active book — Queued, Running, Paused. Excludes
        // terminal states (Completed/Failed/Canceled/Idle/AwaitingReview)
        // because the admin usage page is about "what is the worker
        // actually doing right now."
        group.MapGet("/", async (AppDbContext db, BookCancellationRegistry cancelRegistry) =>
        {
            var rows = await db.Books.AsNoTracking()
                .Include(b => b.User)
                .Where(b => b.Status == BookStatus.Queued
                         || b.Status == BookStatus.Running
                         || b.Status == BookStatus.Paused)
                .OrderBy(b => b.Status == BookStatus.Running ? 0
                            : b.Status == BookStatus.Paused  ? 1 : 2)
                .ThenByDescending(b => b.StartedAt ?? b.CreatedAt)
                .Select(b => new
                {
                    id = b.Id,
                    title = b.Title,
                    fileName = b.OriginalFileName,
                    author = b.Author,
                    ownerEmail = b.User.Email,
                    ownerDisplayName = b.User.DisplayName,
                    status = b.Status.ToString(),
                    model = b.Model,
                    createdAt = b.CreatedAt,
                    startedAt = b.StartedAt,
                })
                .ToListAsync();
            return Results.Ok(rows);
        });
    }
}

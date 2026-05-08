using Tergeo.Server.Data;
using Tergeo.Server.Hubs;
using Tergeo.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace Tergeo.Server.Services;

/// <summary>
/// Persists log lines + status transitions for a book and broadcasts the
/// same payload over the SignalR hub group. Centralizes the "log + push"
/// pattern so callers don't have to repeat the dual-write themselves.
/// </summary>
public sealed class BookEventLogger(IServiceScopeFactory scopes, IHubContext<BookHub> hub)
{
    public async Task LogAsync(
        Guid bookId,
        string level,
        string message,
        CancellationToken ct = default,
        string? detail = null,
        string? groupId = null)
    {
        var entry = new BookLogEntry
        {
            BookId = bookId,
            Level = level,
            Message = message,
            Detail = detail,
            GroupId = groupId,
            Timestamp = DateTimeOffset.UtcNow,
        };

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BookLogs.Add(entry);
        await db.SaveChangesAsync(ct);

        await hub.Clients.Group(BookHub.GroupForBook(bookId)).SendAsync(
            "log",
            new { bookId, entry.Timestamp, entry.Level, entry.Message, entry.Detail, entry.GroupId },
            cancellationToken: ct);
    }

    public async Task UpdateStatusAsync(
        Guid bookId,
        BookStatus status,
        int? progress = null,
        int? done = null,
        int? total = null,
        CancellationToken ct = default)
    {
        await hub.Clients.Group(BookHub.GroupForBook(bookId)).SendAsync(
            "status",
            new { bookId, status = status.ToString(), progress, done, total },
            cancellationToken: ct);
    }
}

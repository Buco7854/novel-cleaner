using NovelCleaner.Server.Data;
using NovelCleaner.Server.Hubs;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Persists log lines + status transitions for a novel and broadcasts the
/// same payload over the SignalR hub group. Centralizes the "log + push"
/// pattern so callers don't have to repeat the dual-write themselves.
/// </summary>
public sealed class NovelEventLogger(IServiceScopeFactory scopes, IHubContext<NovelHub> hub)
{
    public async Task LogAsync(
        Guid novelId,
        string level,
        string message,
        CancellationToken ct = default,
        string? detail = null,
        string? groupId = null)
    {
        var entry = new NovelLogEntry
        {
            NovelId = novelId,
            Level = level,
            Message = message,
            Detail = detail,
            GroupId = groupId,
            Timestamp = DateTimeOffset.UtcNow,
        };

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.NovelLogs.Add(entry);
        await db.SaveChangesAsync(ct);

        await hub.Clients.Group(NovelHub.GroupForNovel(novelId)).SendAsync(
            "log",
            new { novelId, entry.Timestamp, entry.Level, entry.Message, entry.Detail, entry.GroupId },
            cancellationToken: ct);
    }

    public async Task UpdateStatusAsync(
        Guid novelId,
        NovelStatus status,
        int? progress = null,
        int? done = null,
        int? total = null,
        CancellationToken ct = default)
    {
        await hub.Clients.Group(NovelHub.GroupForNovel(novelId)).SendAsync(
            "status",
            new { novelId, status = status.ToString(), progress, done, total },
            cancellationToken: ct);
    }
}

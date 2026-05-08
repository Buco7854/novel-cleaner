using System.Security.Claims;
using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Tergeo.Server.Hubs;

/// <summary>
/// SignalR hub for live book updates — log lines and status transitions
/// emitted by <see cref="Services.BookProcessor"/> and the various endpoints.
/// Clients subscribe per book id; the server pushes events into the matching
/// group as work progresses.
/// </summary>
[Authorize]
public class BookHub(AppDbContext db) : Hub
{
    public async Task SubscribeBook(string bookId)
    {
        if (!Guid.TryParse(bookId, out var id))
            throw new HubException("Invalid book id.");

        if (!await UserCanAccessAsync(id))
            throw new HubException("Not authorized for this book.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForBook(id));
    }

    public async Task UnsubscribeBook(string bookId)
    {
        if (!Guid.TryParse(bookId, out var id)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForBook(id));
    }

    private async Task<bool> UserCanAccessAsync(Guid bookId)
    {
        var principal = Context.User
            ?? throw new HubException("Not authenticated.");
        if (principal.IsInRole(AppRoles.Admin)) return true;

        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId)) return false;

        return await db.Books
            .AsNoTracking()
            .AnyAsync(n => n.Id == bookId && n.UserId == userId);
    }

    public static string GroupForBook(Guid id) => $"book:{id}";
    public static string GroupForBook(string id) => $"book:{id}";
}

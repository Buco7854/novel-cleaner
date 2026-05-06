using System.Security.Claims;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Hubs;

[Authorize]
public class JobHub(AppDbContext db) : Hub
{
    public async Task SubscribeNovel(string novelId)
    {
        if (!Guid.TryParse(novelId, out var id))
            throw new HubException("Invalid novel id.");

        if (!await UserCanAccessAsync(id))
            throw new HubException("Not authorized for this novel.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForNovel(id));
    }

    public async Task UnsubscribeNovel(string novelId)
    {
        if (!Guid.TryParse(novelId, out var id)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForNovel(id));
    }

    private async Task<bool> UserCanAccessAsync(Guid novelId)
    {
        var principal = Context.User
            ?? throw new HubException("Not authenticated.");
        if (principal.IsInRole(AppRoles.Admin)) return true;

        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId)) return false;

        return await db.CleanJobs
            .AsNoTracking()
            .AnyAsync(j => j.Id == novelId && j.UserId == userId);
    }

    public static string GroupForNovel(Guid id) => $"novel:{id}";
    public static string GroupForNovel(string id) => $"novel:{id}";
}

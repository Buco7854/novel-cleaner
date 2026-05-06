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
    public async Task SubscribeJob(string jobId)
    {
        if (!Guid.TryParse(jobId, out var id))
            throw new HubException("Invalid job id.");

        if (!await UserCanAccessAsync(id))
            throw new HubException("Not authorized for this job.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForJob(id));
    }

    public async Task UnsubscribeJob(string jobId)
    {
        if (!Guid.TryParse(jobId, out var id)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForJob(id));
    }

    private async Task<bool> UserCanAccessAsync(Guid jobId)
    {
        var principal = Context.User
            ?? throw new HubException("Not authenticated.");
        if (principal.IsInRole(AppRoles.Admin)) return true;

        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId)) return false;

        return await db.CleanJobs
            .AsNoTracking()
            .AnyAsync(j => j.Id == jobId && j.UserId == userId);
    }

    public static string GroupForJob(Guid id) => $"job:{id}";
    public static string GroupForJob(string id) => $"job:{id}";
}

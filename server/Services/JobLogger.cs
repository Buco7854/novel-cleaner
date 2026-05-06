using NovelCleaner.Server.Data;
using NovelCleaner.Server.Hubs;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace NovelCleaner.Server.Services;

public sealed class JobLogger(IServiceScopeFactory scopes, IHubContext<JobHub> hub)
{
    public async Task LogAsync(Guid jobId, string level, string message, CancellationToken ct = default)
    {
        var entry = new JobLogEntry
        {
            JobId = jobId,
            Level = level,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
        };

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.JobLogs.Add(entry);
        await db.SaveChangesAsync(ct);

        await hub.Clients.Group(JobHub.GroupForJob(jobId)).SendAsync(
            "log",
            new { jobId, entry.Timestamp, entry.Level, entry.Message },
            cancellationToken: ct);
    }

    public async Task UpdateStatusAsync(
        Guid jobId,
        JobStatus status,
        int? progress = null,
        int? done = null,
        int? total = null,
        CancellationToken ct = default)
    {
        await hub.Clients.Group(JobHub.GroupForJob(jobId)).SendAsync(
            "status",
            new { jobId, status = status.ToString(), progress, done, total },
            cancellationToken: ct);
    }
}

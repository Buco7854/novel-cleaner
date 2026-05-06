namespace NovelCleaner.Server.Models;

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}

public class CleanJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = default!;

    public string OriginalFileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string InputStoragePath { get; set; } = "";
    public string? OutputStoragePath { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public bool ScanAll { get; set; }
    public string PatternsJson { get; set; } = "[]";
    public int ContextWindow { get; set; } = 1000;
    public int MaxWorkers { get; set; } = 3;
    public string Model { get; set; } = "";

    public int RemovedCount { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<JobLogEntry> Logs { get; set; } = [];
}

public class JobLogEntry
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public CleanJob Job { get; set; } = default!;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

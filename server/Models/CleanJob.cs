namespace NovelCleaner.Server.Models;

public enum JobStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled,
    // Append new values at the end. EF stores enums as int and existing
    // rows would be misinterpreted if positions shifted.
    AwaitingReview,
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

    /// <summary>
    /// Snapshotted from <see cref="UserSettings.ReviewBeforeApplying"/> at job
    /// creation time. Job-scoped so that flipping the user setting after the
    /// LLM pass started doesn't reroute an in-flight job.
    /// </summary>
    public bool ReviewBeforeApplying { get; set; }

    /// <summary>
    /// Set by the "rerun AI" endpoint to ask the worker to re-run the LLM
    /// identification pass against this same job — without cloning to a new
    /// file. New LLM proposals are appended to the existing ChapterReviews
    /// as Pending so the user explicitly sees what was just found. Cleared
    /// by the worker at the start of every run.
    /// </summary>
    public bool RerunRequested { get; set; }

    /// <summary>
    /// Path to the per-job git repository that backs the editor. One file
    /// per page (visible-text projection), initial commit holds the
    /// untouched extraction. User edits and AI runs land in the working
    /// tree; accept-hunk = commit; reject-hunk = checkout. Null on jobs
    /// created before the git-backed editor existed.
    /// </summary>
    public string? RepoPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<JobLogEntry> Logs { get; set; } = [];
    public ICollection<ChapterReview> Reviews { get; set; } = [];
}

public class JobLogEntry
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public CleanJob Job { get; set; } = default!;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";

    /// <summary>
    /// Optional verbatim payload attached to a log line — typically the raw
    /// LLM response (after basic cleanup like stripping ```json fences) that
    /// produced the line. Frontend renders it inside an expandable panel.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Optional group key. Lines that share a value belong to the same
    /// per-document storyline — the worker uses the document name so all
    /// retry attempts, ✓/✗ items, and the final disposition for chapter05.html
    /// stay together visually even when other docs interleave them in time.
    /// </summary>
    public string? GroupId { get; set; }
}

namespace NovelCleaner.Server.Models;

public enum NovelStatus
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
    /// <summary>
    /// Novel uploaded but no AI run has been triggered yet. Editor is fully
    /// usable (the repo is initialized at upload time); the user explicitly
    /// kicks off the AI from the editor toolbar when ready.
    /// </summary>
    Idle,
}

/// <summary>
/// A novel in a user's library — the unit of work the app revolves around.
/// One row per uploaded EPUB; tracks the file on disk, the editor's git repo,
/// the AI-run lifecycle, and the OPF metadata surfaced in the UI.
/// </summary>
public class Novel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = default!;

    public string OriginalFileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string InputStoragePath { get; set; } = "";
    public string? OutputStoragePath { get; set; }

    // ── EPUB metadata (read from the OPF at upload, editable from the UI).
    // None of these are required — older rows and EPUBs with sparse metadata
    // simply leave them null and the UI falls back to OriginalFileName.
    public string? Title       { get; set; }
    public string? Author      { get; set; }
    public string? Language    { get; set; }
    public string? Publisher   { get; set; }
    public string? Description { get; set; }

    public NovelStatus Status { get; set; } = NovelStatus.Queued;
    public int MaxWorkers { get; set; } = 3;
    public string Model { get; set; } = "";

    public int RemovedCount { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Per-novel additional instructions appended after the admin and
    /// user prompts at every LLM call. Lets the user say "this book is
    /// in French", "preserve em-dashes", etc. without polluting their
    /// global preferences.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Set by the "rerun AI" endpoint to ask the worker to re-run the LLM
    /// identification pass against this same novel — without cloning to a new
    /// file. The fresh suggestions land in the editor's working tree on top
    /// of HEAD so the user explicitly sees what was just found. Cleared by
    /// the worker at the start of every run.
    /// </summary>
    public bool RerunRequested { get; set; }

    /// <summary>
    /// Optional JSON array of page paths (e.g. <c>["pages/0001_foo.txt"]</c>)
    /// the next AI run should be limited to. Set by the editor's "Run AI on
    /// selected pages" button; cleared by the worker at the start of every
    /// run so subsequent reruns default back to whole-book.
    /// </summary>
    public string? PagesFilterJson { get; set; }

    /// <summary>
    /// Path to the per-novel git repository that backs the editor. One file
    /// per page (visible-text projection), initial commit holds the
    /// untouched extraction. User edits and AI runs land in the working
    /// tree; accept-hunk = commit; reject-hunk = checkout. Null on novels
    /// created before the git-backed editor existed.
    /// </summary>
    public string? RepoPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<NovelLogEntry> Logs { get; set; } = [];
}

/// <summary>
/// One log line scoped to a single novel — surfaced live over SignalR and
/// persisted so a refresh repopulates the editor's log panel.
/// </summary>
public class NovelLogEntry
{
    public long Id { get; set; }
    public Guid NovelId { get; set; }
    public Novel Novel { get; set; } = default!;

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

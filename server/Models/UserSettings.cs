using System.ComponentModel.DataAnnotations;

namespace NovelCleaner.Server.Models;

/// <summary>
/// Per-user preferences. Cost / credentials live on <see cref="AppSettings"/>.
/// </summary>
public class UserSettings
{
    [Key]
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = default!;

    /// <summary>JSON-encoded list of regex/literal trigger patterns.</summary>
    public string PatternsJson { get; set; } = "[]";

    /// <summary>Default scan mode for new jobs (true = full-page, false = pattern detection).</summary>
    public bool ScanAll { get; set; }

    /// <summary>Characters of context to include around each pattern match.</summary>
    public int ContextWindow { get; set; } = 1000;

    /// <summary>
    /// When true, completed LLM passes pause at <c>AwaitingReview</c> so the
    /// user can accept/reject/add proposals before the cleaned EPUB is
    /// written. Default true — destructive edits should be opt-out.
    /// </summary>
    public bool ReviewBeforeApplying { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

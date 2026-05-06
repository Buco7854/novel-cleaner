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

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

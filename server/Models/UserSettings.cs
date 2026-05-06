using System.ComponentModel.DataAnnotations;

namespace NovelCleaner.Server.Models;

/// <summary>
/// Per-user preferences. Cost / credentials live on <see cref="AppSettings"/>.
/// Currently nothing user-facing — the table stays so future preferences
/// can hang off it without an extra migration.
/// </summary>
public class UserSettings
{
    [Key]
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = default!;

    /// <summary>
    /// Optional per-user instructions that get appended to the admin's
    /// system prompt before each LLM call. The admin's prompt is shared
    /// across all users; this is the user's personal extension to it
    /// (e.g. "preserve all em-dashes", "this book is in French").
    /// </summary>
    public string? SystemPrompt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

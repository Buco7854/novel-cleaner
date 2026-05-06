using System.ComponentModel.DataAnnotations;

namespace NovelCleaner.Server.Models;

/// <summary>
/// Global, admin-managed configuration. A single row identified by
/// <see cref="SingletonKey"/> holds the LLM credentials, output prompt,
/// concurrency cap, and drop-folder path for the whole instance.
/// </summary>
public class AppSettings
{
    public const int SingletonKey = 1;

    [Key]
    public int Id { get; set; } = SingletonKey;

    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>Concurrent LLM requests per job. Affects cost / rate limits, hence admin-scoped.</summary>
    public int MaxWorkers { get; set; } = 3;

    /// <summary>Optional additional instructions appended to the locked output-format prompt.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Optional absolute path to copy each cleaned EPUB to once the job finishes.</summary>
    public string? DropFolder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace Tergeo.Server.Models;

/// <summary>
/// What happens when the user adds a book from this OPDS feed.
/// <see cref="AddOnly"/> imports without running the AI; <see cref="AddAndRunAi"/>
/// imports and queues the cleanup pass. The browse page surfaces both
/// buttons equally per book — the source itself doesn't pick a default.
/// </summary>
public enum OpdsImportMode
{
    AddOnly = 0,
    AddAndRunAi = 1,
}

public class OpdsSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = default!;

    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Username { get; set; }
    public string? PasswordCipher { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

namespace NovelCleaner.Server.Models;

public class OpdsSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = default!;

    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Username { get; set; }
    public string? PasswordCipher { get; set; }
    public bool AutoClean { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

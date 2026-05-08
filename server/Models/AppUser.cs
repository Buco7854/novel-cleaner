using Microsoft.AspNetCore.Identity;

namespace NovelCleaner.Server.Models;

public class AppUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
    public string Provider { get; set; } = "local";
    public string? ExternalSubject { get; set; }
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    public UserSettings? Settings { get; set; }
    public ICollection<Novel> Novels { get; set; } = [];
}

public class AppRole : IdentityRole<Guid>
{
    public AppRole() { }
    public AppRole(string name) : base(name) { }
}

public static class AppRoles
{
    public const string Admin    = "Admin";
    public const string User     = "User";
    /// <summary>Permission to use the configured drop folder for cleaned novels.</summary>
    public const string DropFolder = "DropFolder";
}

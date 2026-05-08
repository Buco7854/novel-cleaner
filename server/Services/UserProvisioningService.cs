using System.Security.Claims;
using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

public sealed class UserProvisioningService(
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    AppDbContext db,
    IOptionsMonitor<AuthOptions> auth,
    ILogger<UserProvisioningService> log)
{
    public async Task<AppUser?> ProvisionFromOidcAsync(ClaimsPrincipal principal, string provider = "oidc")
    {
        var opts = auth.CurrentValue.Oidc;
        if (!opts.Enabled) return null;

        var sub = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(sub)) { log.LogWarning("OIDC principal missing sub"); return null; }

        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email");
        var name = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("name") ?? email ?? sub;

        // Group mapping is OPTIONAL.
        //  - AdminGroups empty       → admin role is not managed by OIDC (manual promotions stick).
        //  - AdminGroups non-empty   → members of any listed group get Admin; non-members lose it.
        //  - DropFolderGroups empty  → DropFolder role is not managed by OIDC.
        //  - DropFolderGroups non-empty → members of any listed group get DropFolder; non-members lose it.
        // Every authenticated OIDC user always gets the User role.
        var groupSet = principal.FindAll(opts.GroupsClaim)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var adminMappingConfigured = opts.AdminGroups.Count > 0;
        var isAdmin = adminMappingConfigured && groupSet.Overlaps(opts.AdminGroups);

        var dropMappingConfigured = opts.DropFolderGroups.Count > 0;
        var hasDropPermission = dropMappingConfigured && groupSet.Overlaps(opts.DropFolderGroups);

        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.Provider == provider && u.ExternalSubject == sub);

        if (user is null && !string.IsNullOrEmpty(email))
            user = await users.FindByEmailAsync(email);

        if (user is null)
        {
            if (!opts.AutoProvision) { log.LogInformation("Auto-provision disabled, skipping {Sub}", sub); return null; }

            user = new AppUser
            {
                UserName = email ?? sub,
                Email = email,
                EmailConfirmed = !string.IsNullOrEmpty(email),
                DisplayName = name,
                Provider = provider,
                ExternalSubject = sub,
            };
            var create = await users.CreateAsync(user);
            if (!create.Succeeded)
            {
                log.LogWarning("Failed to create user from OIDC: {Errors}",
                    string.Join("; ", create.Errors.Select(e => e.Description)));
                return null;
            }
            await EnsureSettingsAsync(user.Id);
        }
        else if (string.IsNullOrEmpty(user.ExternalSubject))
        {
            user.ExternalSubject = sub;
            user.Provider = provider;
        }

        user.DisplayName = name;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await SyncRolesAsync(user, isAdmin, adminMappingConfigured, hasDropPermission, dropMappingConfigured);
        return user;
    }

    public async Task EnsureSettingsAsync(Guid userId)
    {
        if (await db.UserSettings.AnyAsync(s => s.UserId == userId)) return;
        db.UserSettings.Add(new UserSettings { UserId = userId });
        await db.SaveChangesAsync();
    }

    private async Task SyncRolesAsync(
        AppUser user,
        bool isAdmin, bool adminMappingConfigured,
        bool hasDropPermission, bool dropMappingConfigured)
    {
        foreach (var role in new[] { AppRoles.Admin, AppRoles.User, AppRoles.DropFolder })
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new AppRole(role));

        var current = (await users.GetRolesAsync(user)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Admin role: only sync when admin group mapping is configured.
        // Otherwise leave whatever was set manually (in /admin/users).
        if (adminMappingConfigured)
        {
            if (isAdmin && !current.Contains(AppRoles.Admin))
                await users.AddToRoleAsync(user, AppRoles.Admin);
            else if (!isAdmin && current.Contains(AppRoles.Admin))
                await users.RemoveFromRoleAsync(user, AppRoles.Admin);
        }

        // DropFolder role: same opt-in semantics as Admin. When
        // DefaultDropFolder is true, this is irrelevant (everyone has the
        // permission anyway), but the role membership is still kept in sync
        // for auditability.
        if (dropMappingConfigured)
        {
            if (hasDropPermission && !current.Contains(AppRoles.DropFolder))
                await users.AddToRoleAsync(user, AppRoles.DropFolder);
            else if (!hasDropPermission && current.Contains(AppRoles.DropFolder))
                await users.RemoveFromRoleAsync(user, AppRoles.DropFolder);
        }

        // User role: always granted to anyone who successfully signed in.
        if (!current.Contains(AppRoles.User))
            await users.AddToRoleAsync(user, AppRoles.User);
    }
}

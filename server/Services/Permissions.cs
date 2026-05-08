using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace NovelCleaner.Server.Services;

public static class Permissions
{
    /// <summary>
    /// Whether <paramref name="user"/> may have cleaned novels copied to the
    /// admin-configured drop folder. Admins always can. When
    /// <see cref="AuthOptions.DefaultDropFolder"/> is true, every authenticated
    /// user can. Otherwise, only users with the <c>DropFolder</c> role.
    /// </summary>
    public static async Task<bool> CanUseDropFolderAsync(
        UserManager<AppUser> users, AppUser user, AuthOptions auth)
    {
        if (auth.DefaultDropFolder) return true;
        var roles = await users.GetRolesAsync(user);
        return roles.Contains(AppRoles.Admin) || roles.Contains(AppRoles.DropFolder);
    }
}

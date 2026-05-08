using Tergeo.Server.Configuration;
using Tergeo.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace Tergeo.Server.Data;

public static class SeedData
{
    public static async Task EnsureRolesAsync(IServiceProvider sp)
    {
        var roles = sp.GetRequiredService<RoleManager<AppRole>>();
        foreach (var name in new[] { AppRoles.Admin, AppRoles.User, AppRoles.DropFolder })
            if (!await roles.RoleExistsAsync(name))
                await roles.CreateAsync(new AppRole(name));
    }

    public static async Task EnsureFirstAdminAsync(IServiceProvider sp, AuthOptions auth)
    {
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var existing = await users.FindByEmailAsync(auth.FirstAdminEmail!);
        if (existing is not null) return;

        var admin = new AppUser
        {
            UserName = auth.FirstAdminEmail!,
            Email = auth.FirstAdminEmail!,
            EmailConfirmed = true,
            DisplayName = "Administrator",
            Provider = "local",
        };
        var result = await users.CreateAsync(admin, auth.FirstAdminPassword!);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Could not seed admin: " + string.Join(", ", result.Errors.Select(e => e.Description)));

        await users.AddToRolesAsync(admin, [AppRoles.Admin, AppRoles.User]);
    }
}

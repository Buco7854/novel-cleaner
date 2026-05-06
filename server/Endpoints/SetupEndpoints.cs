using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NovelCleaner.Server.Models;

namespace NovelCleaner.Server.Endpoints;

public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/setup");

        g.MapGet("/needed", async (UserManager<AppUser> users) =>
        {
            var admins = await users.GetUsersInRoleAsync(AppRoles.Admin);
            return Results.Ok(new { needed = admins.Count == 0 });
        });

        g.MapPost("/", async (
            [FromBody] SetupRequest req,
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager) =>
        {
            var admins = await userManager.GetUsersInRoleAsync(AppRoles.Admin);
            if (admins.Count > 0)
                return Results.BadRequest(new { error = "Setup has already been completed." });

            var user = new AppUser
            {
                UserName = req.Email,
                Email = req.Email,
                DisplayName = req.DisplayName?.Trim() is { Length: > 0 } dn ? dn : null,
                CreatedAt = DateTimeOffset.UtcNow,
                LastLoginAt = DateTimeOffset.UtcNow,
            };

            var result = await userManager.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return Results.BadRequest(new { error = result.Errors.FirstOrDefault()?.Description ?? "Could not create user." });

            await userManager.AddToRolesAsync(user, [AppRoles.Admin, AppRoles.User]);
            await signInManager.SignInAsync(user, isPersistent: true);

            return Results.Ok(new { ok = true });
        });
    }

    public sealed record SetupRequest(string Email, string Password, string? DisplayName);
}

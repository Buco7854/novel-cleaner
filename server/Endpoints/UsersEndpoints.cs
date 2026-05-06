using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

public static class UsersEndpoints
{
    public static void MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization(p => p.RequireRole(AppRoles.Admin));

        group.MapGet("/", async (AppDbContext db, UserManager<AppUser> users) =>
        {
            var list = await db.Users.OrderBy(u => u.Email).ToListAsync();
            var result = new List<object>(list.Count);
            foreach (var u in list)
            {
                var roles = await users.GetRolesAsync(u);
                result.Add(new
                {
                    id = u.Id,
                    email = u.Email,
                    displayName = u.DisplayName,
                    provider = u.Provider,
                    isDisabled = u.IsDisabled,
                    createdAt = u.CreatedAt,
                    lastLoginAt = u.LastLoginAt,
                    roles,
                });
            }
            return Results.Ok(result);
        });

        group.MapPost("/", async (
            [FromBody] CreateUserRequest req,
            UserManager<AppUser> users,
            RoleManager<AppRole> roles,
            AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "email required" });
            if (string.IsNullOrWhiteSpace(req.Password)) return Results.BadRequest(new { error = "password required" });

            var user = new AppUser
            {
                UserName = req.Email,
                Email = req.Email,
                EmailConfirmed = true,
                DisplayName = req.DisplayName ?? req.Email,
                Provider = "local",
            };
            var create = await users.CreateAsync(user, req.Password);
            if (!create.Succeeded)
                return Results.BadRequest(new { errors = create.Errors.Select(e => e.Description) });

            foreach (var role in new[] { AppRoles.Admin, AppRoles.User })
                if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new AppRole(role));

            var assigned = req.IsAdmin ? new[] { AppRoles.Admin, AppRoles.User } : new[] { AppRoles.User };
            await users.AddToRolesAsync(user, assigned);

            db.UserSettings.Add(new UserSettings { UserId = user.Id });
            await db.SaveChangesAsync();

            return Results.Ok(new { id = user.Id });
        });

        group.MapPatch("/{id:guid}", async (Guid id, [FromBody] UpdateUserRequest req,
            UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            if (req.DisplayName is not null) user.DisplayName = req.DisplayName;
            if (req.IsDisabled.HasValue) user.IsDisabled = req.IsDisabled.Value;
            await users.UpdateAsync(user);

            if (req.IsAdmin.HasValue)
            {
                var inAdmin = await users.IsInRoleAsync(user, AppRoles.Admin);
                if (req.IsAdmin.Value && !inAdmin) await users.AddToRoleAsync(user, AppRoles.Admin);
                if (!req.IsAdmin.Value && inAdmin) await users.RemoveFromRoleAsync(user, AppRoles.Admin);
            }

            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/password", async (Guid id, [FromBody] PasswordRequest req,
            UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var result = await users.ResetPasswordAsync(user, token, req.Password);
            return result.Succeeded
                ? Results.NoContent()
                : Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        });

        group.MapDelete("/{id:guid}", async (Guid id, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            await users.DeleteAsync(user);
            return Results.NoContent();
        });
    }

    public sealed record CreateUserRequest(string Email, string Password, string? DisplayName, bool IsAdmin);
    public sealed record UpdateUserRequest(string? DisplayName, bool? IsDisabled, bool? IsAdmin);
    public sealed record PasswordRequest(string Password);
}

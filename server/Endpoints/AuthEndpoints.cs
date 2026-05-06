using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/me", async (HttpContext http, UserManager<AppUser> users) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return Results.Ok(new { authenticated = false });
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.Ok(new { authenticated = false });
            var roles = await users.GetRolesAsync(user);
            return Results.Ok(new
            {
                authenticated = true,
                user = new
                {
                    id = user.Id,
                    email = user.Email,
                    displayName = user.DisplayName ?? user.UserName,
                    provider = user.Provider,
                    roles,
                },
            });
        });

        group.MapPost("/login", async (
            [FromBody] LoginRequest req,
            SignInManager<AppUser> signIn,
            UserManager<AppUser> users,
            IOptionsMonitor<AuthOptions> opts) =>
        {
            if (!opts.CurrentValue.Password.Enabled)
                return Results.NotFound(new { error = "Password authentication is disabled" });

            var user = await users.FindByEmailAsync(req.Email)
                    ?? await users.FindByNameAsync(req.Email);
            if (user is null || user.IsDisabled) return Results.Unauthorized();

            var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: true, lockoutOnFailure: true);
            if (!result.Succeeded) return Results.Unauthorized();
            user.LastLoginAt = DateTimeOffset.UtcNow;
            await users.UpdateAsync(user);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/logout", async (HttpContext http, IOptionsMonitor<AuthOptions> opts) =>
        {
            await http.SignOutAsync(IdentityConstants.ApplicationScheme);
            if (opts.CurrentValue.Oidc.Enabled && http.User.HasClaim(c => c.Type == "oidc_session"))
                await http.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/oidc/login", (HttpContext http, IOptionsMonitor<AuthOptions> opts, [FromQuery] string? returnUrl) =>
        {
            if (!opts.CurrentValue.Oidc.Enabled) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = returnUrl ?? "/" };
            return Results.Challenge(props, [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        group.MapGet("/oidc/logout", async (HttpContext http, IOptionsMonitor<AuthOptions> opts) =>
        {
            await http.SignOutAsync(IdentityConstants.ApplicationScheme);
            if (opts.CurrentValue.Oidc.Enabled)
                await http.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
                    new AuthenticationProperties { RedirectUri = "/" });
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/config", (IOptionsMonitor<AuthOptions> opts) => Results.Ok(new
        {
            password = opts.CurrentValue.Password.Enabled,
            oidc = opts.CurrentValue.Oidc.Enabled,
            oidcDisplayName = opts.CurrentValue.Oidc.DisplayName,
            allowSelfRegister = opts.CurrentValue.Password.AllowSelfRegister,
        }));
    }

    public sealed record LoginRequest(string Email, string Password);
}

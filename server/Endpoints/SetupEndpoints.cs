using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NovelCleaner.Server.Models;

namespace NovelCleaner.Server.Endpoints;

public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        // Serve the SPA when it's built, otherwise fall back to the inline form.
        // This is the redirect target used by the setup-guard middleware.
        app.MapGet("/setup", (IWebHostEnvironment env) =>
        {
            var index = env.WebRootFileProvider.GetFileInfo("index.html");
            return index.Exists
                ? Results.File(index.PhysicalPath!, "text/html")
                : Results.Content(SetupHtml, "text/html");
        }).ExcludeFromDescription();

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

    private const string SetupHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Novel Cleaner — Setup</title>
          <style>
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: system-ui, sans-serif; background: #fafaf9; color: #1c1917;
                   min-height: 100dvh; display: grid; place-items: center; padding: 1rem; }
            .card { background: #fff; border: 1px solid #e7e5e4; border-radius: 10px;
                    padding: 2rem; width: 100%; max-width: 360px; }
            h1 { font-size: 1.125rem; font-weight: 600; margin-bottom: .25rem; }
            p  { font-size: .875rem; color: #78716c; margin-bottom: 1.5rem; }
            label { display: block; font-size: .8125rem; font-weight: 500; margin-bottom: .35rem; }
            input { display: block; width: 100%; padding: .5rem .75rem; font-size: .875rem;
                    border: 1px solid #d6d3d1; border-radius: 6px; outline: none; margin-bottom: 1rem; }
            input:focus { border-color: #a8a29e; box-shadow: 0 0 0 2px #e7e5e4; }
            button { width: 100%; padding: .55rem; font-size: .875rem; font-weight: 500;
                     background: #1c1917; color: #fff; border: none; border-radius: 6px;
                     cursor: pointer; margin-top: .25rem; }
            button:disabled { opacity: .5; cursor: default; }
            .err { margin-top: .75rem; padding: .6rem .75rem; border-radius: 6px;
                   font-size: .8125rem; background: #fff1f2; color: #be123c;
                   border: 1px solid #fecdd3; display: none; }
          </style>
        </head>
        <body>
          <div class="card">
            <h1>First-time setup</h1>
            <p>Create the administrator account for this instance.</p>
            <form id="f">
              <label for="dn">Display name</label>
              <input id="dn" type="text" placeholder="Your name" autocomplete="name" />
              <label for="em">Email <span style="color:#ef4444">*</span></label>
              <input id="em" type="email" required placeholder="admin@example.com" autocomplete="email" />
              <label for="pw">Password <span style="color:#ef4444">*</span></label>
              <input id="pw" type="password" required minlength="10" placeholder="At least 10 characters" autocomplete="new-password" />
              <label for="pw2">Confirm password <span style="color:#ef4444">*</span></label>
              <input id="pw2" type="password" required placeholder="Repeat password" autocomplete="new-password" />
              <button type="submit" id="btn">Create account</button>
              <div class="err" id="err"></div>
            </form>
          </div>
          <script>
            document.getElementById('f').addEventListener('submit', async e => {
              e.preventDefault();
              const btn = document.getElementById('btn');
              const err = document.getElementById('err');
              const email = document.getElementById('em').value;
              const pw    = document.getElementById('pw').value;
              const pw2   = document.getElementById('pw2').value;
              const dn    = document.getElementById('dn').value;
              err.style.display = 'none';
              if (pw !== pw2) { err.textContent = 'Passwords do not match.'; err.style.display = 'block'; return; }
              btn.disabled = true; btn.textContent = 'Creating…';
              try {
                const r = await fetch('/api/setup', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ email, password: pw, displayName: dn || undefined }),
                });
                if (r.ok) { location.href = '/'; return; }
                const d = await r.json().catch(() => ({}));
                err.textContent = d.error ?? 'Setup failed. Please try again.';
                err.style.display = 'block';
              } finally {
                btn.disabled = false; btn.textContent = 'Create account';
              }
            });
          </script>
        </body>
        </html>
        """;
}

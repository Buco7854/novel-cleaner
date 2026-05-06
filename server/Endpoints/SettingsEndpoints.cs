using System.Security.Claims;
using System.Text.Json;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization();

        // Combined GET — returns both app (admin-managed) and user (per-user)
        // settings. Any authenticated user can read; the API key is masked
        // behind a `hasApiKey` boolean.
        group.MapGet("/", async (HttpContext http, AppDbContext db) =>
        {
            var userId = GetUserId(http);
            var appS = await GetOrCreateAppAsync(db);
            var userS = await GetOrCreateUserAsync(db, userId);
            return Results.Ok(BuildDto(appS, userS, isAdmin: http.User.IsInRole(AppRoles.Admin)));
        });

        // Admin-only PUT for the global section.
        group.MapPut("/app", async (HttpContext http, AppDbContext db, [FromBody] AppSettingsDto dto) =>
        {
            if (!http.User.IsInRole(AppRoles.Admin)) return Results.Forbid();
            var s = await GetOrCreateAppAsync(db);

            if (dto.ApiKey is not null) s.ApiKey = dto.ApiKey;
            s.BaseUrl = dto.BaseUrl ?? "";
            s.Model = dto.Model ?? "";
            s.MaxWorkers = Math.Clamp(dto.MaxWorkers, 1, 10);
            s.SystemPrompt = NormalizePrompt(dto.SystemPrompt);
            s.DropFolder = string.IsNullOrWhiteSpace(dto.DropFolder) ? null : dto.DropFolder.Trim();
            s.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            var userS = await GetOrCreateUserAsync(db, GetUserId(http));
            return Results.Ok(BuildDto(s, userS, isAdmin: true));
        });

        // Self-service PUT for the per-user section.
        group.MapPut("/user", async (HttpContext http, AppDbContext db, [FromBody] UserSettingsDto dto) =>
        {
            var userId = GetUserId(http);
            var s = await GetOrCreateUserAsync(db, userId);
            s.PatternsJson = JsonSerializer.Serialize(dto.Patterns ?? []);
            s.ScanAll = dto.ScanAll;
            s.ContextWindow = Math.Max(100, dto.ContextWindow);
            s.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            var appS = await GetOrCreateAppAsync(db);
            return Results.Ok(BuildDto(appS, s, isAdmin: http.User.IsInRole(AppRoles.Admin)));
        });
    }

    private static Guid GetUserId(HttpContext http) =>
        Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<AppSettings> GetOrCreateAppAsync(AppDbContext db)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey);
        if (s is null)
        {
            s = new AppSettings { Id = AppSettings.SingletonKey };
            db.AppSettings.Add(s);
            await db.SaveChangesAsync();
        }
        return s;
    }

    private static async Task<UserSettings> GetOrCreateUserAsync(AppDbContext db, Guid userId)
    {
        var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId);
        if (s is null)
        {
            s = new UserSettings { UserId = userId };
            db.UserSettings.Add(s);
            await db.SaveChangesAsync();
        }
        return s;
    }

    private static object BuildDto(AppSettings appS, UserSettings userS, bool isAdmin) => new
    {
        // App-scoped (admin-managed) — read-only for non-admins.
        app = new
        {
            baseUrl = appS.BaseUrl,
            model = appS.Model,
            hasApiKey = !string.IsNullOrEmpty(appS.ApiKey),
            maxWorkers = appS.MaxWorkers,
            systemPrompt = appS.SystemPrompt ?? "",
            dropFolder = appS.DropFolder ?? "",
            defaultSystemPrompt = OpenAiClient.DefaultSystemPrompt,
            canEdit = isAdmin,
        },
        // User-scoped — always editable by the calling user.
        user = new
        {
            patterns = JsonSerializer.Deserialize<List<string>>(userS.PatternsJson) ?? [],
            scanAll = userS.ScanAll,
            contextWindow = userS.ContextWindow,
        },
    };

    private static string? NormalizePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var trimmed = prompt.TrimEnd();
        return trimmed == OpenAiClient.DefaultSystemPrompt.TrimEnd() ? null : trimmed;
    }

    public sealed record AppSettingsDto(
        string? ApiKey,
        string? BaseUrl,
        string? Model,
        int MaxWorkers,
        string? SystemPrompt,
        string? DropFolder);

    public sealed record UserSettingsDto(
        List<string>? Patterns,
        bool ScanAll,
        int ContextWindow);
}

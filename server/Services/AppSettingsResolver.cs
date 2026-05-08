using Tergeo.Server.Configuration;
using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Tergeo.Server.Services;

/// <summary>
/// Effective global settings — env-bound overrides layered on top of the DB
/// row. <see cref="ManagedByEnv"/> tells the frontend which fields are
/// read-only because an operator pinned them via configuration.
/// </summary>
public sealed record ResolvedAppSettings(
    string? ApiKey,
    string BaseUrl,
    string Model,
    int MaxWorkers,
    string? SystemPrompt,
    string? DropFolder,
    bool AiEnabled,
    AppSettingsManagedByEnv ManagedByEnv);

public sealed record AppSettingsManagedByEnv(
    bool ApiKey,
    bool BaseUrl,
    bool Model,
    bool MaxWorkers,
    bool SystemPrompt,
    bool DropFolder,
    bool AiEnabled);

/// <summary>
/// Computes the effective <see cref="ResolvedAppSettings"/> by merging the
/// env-bound <see cref="AppSettingsOverrides"/> on top of the persisted
/// <see cref="AppSettings"/> singleton. Env wins per-field; missing /
/// whitespace env values pass through to the DB.
/// </summary>
public sealed class AppSettingsResolver(
    AppDbContext db,
    IOptionsMonitor<AppSettingsOverrides> envOpts)
{
    public async Task<ResolvedAppSettings> ResolveAsync(CancellationToken ct = default)
    {
        var dbRow = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey, ct)
            ?? new AppSettings { Id = AppSettings.SingletonKey };
        return ResolveFrom(envOpts.CurrentValue, dbRow);
    }

    public static ResolvedAppSettings ResolveFrom(AppSettingsOverrides env, AppSettings dbRow)
    {
        // Empty / whitespace counts as unset — `${VAR:-}` in docker-compose
        // expands to "" and we don't want that to clobber a real DB value.
        static bool HasStr(string? s) => !string.IsNullOrWhiteSpace(s);

        return new ResolvedAppSettings(
            ApiKey:       HasStr(env.ApiKey)       ? env.ApiKey!.Trim()       : dbRow.ApiKey,
            BaseUrl:      HasStr(env.BaseUrl)      ? env.BaseUrl!.Trim()      : dbRow.BaseUrl,
            Model:        HasStr(env.Model)        ? env.Model!.Trim()        : dbRow.Model,
            MaxWorkers:   env.MaxWorkers is > 0    ? env.MaxWorkers.Value     : dbRow.MaxWorkers,
            SystemPrompt: HasStr(env.SystemPrompt) ? env.SystemPrompt!.Trim() : dbRow.SystemPrompt,
            DropFolder:   HasStr(env.DropFolder)   ? env.DropFolder!.Trim()   : dbRow.DropFolder,
            AiEnabled:    env.AiEnabled            ?? dbRow.AiEnabled,
            ManagedByEnv: new AppSettingsManagedByEnv(
                ApiKey:       HasStr(env.ApiKey),
                BaseUrl:      HasStr(env.BaseUrl),
                Model:        HasStr(env.Model),
                MaxWorkers:   env.MaxWorkers is > 0,
                SystemPrompt: HasStr(env.SystemPrompt),
                DropFolder:   HasStr(env.DropFolder),
                AiEnabled:    env.AiEnabled.HasValue));
    }
}

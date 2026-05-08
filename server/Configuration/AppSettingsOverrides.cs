namespace Tergeo.Server.Configuration;

/// <summary>
/// Optional environment-bound overrides for the global admin settings
/// otherwise stored in the <c>AppSettings</c> DB row. Bound from the
/// <c>App:</c> configuration section so values can be supplied via env
/// vars (<c>App__ApiKey</c>, <c>App__BaseUrl</c>, …) or the friendlier
/// <c>TERGEO_*</c> aliases that <c>docker-compose.yml</c> maps onto them.
///
/// Each property is nullable so "not set" is distinguishable from an
/// explicit value. Empty / whitespace strings are treated as not set,
/// because docker-compose's <c>${VAR:-}</c> default expands to an empty
/// string when the operator hasn't filled the env var in.
/// </summary>
public class AppSettingsOverrides
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public int? MaxWorkers { get; set; }
    public string? SystemPrompt { get; set; }
    public string? DropFolder { get; set; }
    public bool? AiEnabled { get; set; }
}

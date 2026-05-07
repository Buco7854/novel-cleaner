using System.Net;
using System.Security.Claims;
using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Endpoints;
using NovelCleaner.Server.Hubs;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console());

// ----- Options ----------------------------------------------------------
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));

// ForwardedHeadersOptions still uses Microsoft.AspNetCore.HttpOverrides.IPNetwork
// even though the type is marked obsolete. Silence the deprecation noise.
#pragma warning disable ASPDEPR005

// Honor X-Forwarded-Proto / X-Forwarded-For from a reverse proxy so the OIDC
// redirect URI is built with the public https:// scheme rather than the
// internal http:// the proxy talks to.
//
// Defaults trust loopback and RFC 1918 / IPv6-ULA private ranges, which covers
// docker bridges, k8s pod networks, and typical home-LAN setups. Extra ranges
// (e.g. a public-facing proxy IP) can be added via the ForwardedHeaders:
// KnownNetworks / KnownProxies config sections — comma-separated, accepting
// either bare IPs (1.2.3.4) or CIDRs (1.2.3.0/24). To trust *every* proxy,
// set KnownNetworks to "0.0.0.0/0,::/0".
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                       | ForwardedHeaders.XForwardedProto
                       | ForwardedHeaders.XForwardedHost;

    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();

    // Loopback
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("127.0.0.0"), 8));
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.IPv6Loopback, 128));
    // RFC 1918
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    // IPv6 ULA (fc00::/7)
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("fc00::"), 7));

    foreach (var cidr in SplitCsv(builder.Configuration["ForwardedHeaders:KnownNetworks"]))
        if (TryParseCidr(cidr, out var network)) o.KnownNetworks.Add(network);

    foreach (var ip in SplitCsv(builder.Configuration["ForwardedHeaders:KnownProxies"]))
        if (IPAddress.TryParse(ip, out var addr)) o.KnownProxies.Add(addr);

    o.ForwardLimit = null; // accept arbitrary proxy chain length (k8s ingress + service mesh, etc.)

    static IEnumerable<string> SplitCsv(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool TryParseCidr(string cidr, out Microsoft.AspNetCore.HttpOverrides.IPNetwork net)
    {
        net = default!;
        var slash = cidr.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(cidr, out var addr)) return false;
            var bits = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            net = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(addr, bits);
            return true;
        }
        if (!IPAddress.TryParse(cidr[..slash], out var prefix)) return false;
        if (!int.TryParse(cidr[(slash + 1)..], out var length)) return false;
        net = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length);
        return true;
    }
});
#pragma warning restore ASPDEPR005

var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();

// Lockout guard: if password auth was turned off but OIDC isn't actually usable,
// force password auth back on so the instance doesn't become unreachable.
static bool IsOidcUsable(OidcOptions o) =>
    o.Enabled
    && !string.IsNullOrWhiteSpace(o.ClientId)
    && (!string.IsNullOrWhiteSpace(o.Authority)
        || !string.IsNullOrWhiteSpace(o.MetadataAddress)
        || o.HasManualEndpoints);

if (!auth.Password.Enabled && !IsOidcUsable(auth.Oidc))
{
    Console.Error.WriteLine(
        "[WARN] Password auth is disabled but OIDC is not configured (need Auth:Oidc:Enabled, " +
        "ClientId, and an Authority / MetadataAddress / manual endpoints). Forcing password auth " +
        "back on so the instance remains reachable. Set Auth:Password:Enabled=true explicitly to " +
        "silence this warning, or finish configuring OIDC.");
    auth.Password.Enabled = true;
}
// Also override the IOptions registration so endpoints reading via IOptionsMonitor
// see the same effective value.
builder.Services.PostConfigure<AuthOptions>(o => { o.Password.Enabled = auth.Password.Enabled; });

// ----- Database (SQLite only, lives in StorageOptions.DataDirectory) -----
Directory.CreateDirectory(storage.DataDirectory);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? $"Data Source={Path.Combine(storage.DataDirectory, "novelcleaner.db")}";

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));

// ----- Identity ---------------------------------------------------------
builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<AppRole>()
    .AddSignInManager()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// ----- Authentication ---------------------------------------------------
var authBuilder = builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = IdentityConstants.ApplicationScheme;
        o.DefaultChallengeScheme = auth.Oidc.Enabled
            ? OpenIdConnectDefaults.AuthenticationScheme
            : IdentityConstants.ApplicationScheme;
    })
    .AddCookie(IdentityConstants.ApplicationScheme, o =>
    {
        o.Cookie.Name = "novelcleaner.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddCookie(IdentityConstants.ExternalScheme);

if (auth.Oidc.Enabled)
{
    authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        // Three discovery modes, in priority order:
        //   1. HasManualEndpoints      → fully static, no IdP round-trip
        //   2. MetadataAddress is set  → fetch this exact discovery URL
        //   3. otherwise               → use Authority + standard well-known path
        if (auth.Oidc.HasManualEndpoints)
        {
            o.Authority = auth.Oidc.Issuer ?? auth.Oidc.Authority;
            o.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
            {
                Issuer = auth.Oidc.Issuer ?? auth.Oidc.Authority,
                AuthorizationEndpoint = auth.Oidc.AuthorizationEndpoint!,
                TokenEndpoint = auth.Oidc.TokenEndpoint!,
                UserInfoEndpoint = auth.Oidc.UserInfoEndpoint ?? "",
                JwksUri = auth.Oidc.JwksUri!,
                EndSessionEndpoint = auth.Oidc.EndSessionEndpoint ?? "",
            };
        }
        else if (!string.IsNullOrWhiteSpace(auth.Oidc.MetadataAddress))
        {
            o.Authority = auth.Oidc.Authority;
            o.MetadataAddress = auth.Oidc.MetadataAddress;
        }
        else
        {
            o.Authority = auth.Oidc.Authority;
        }

        o.ClientId = auth.Oidc.ClientId;
        o.ClientSecret = auth.Oidc.ClientSecret;
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.UsePkce = true;
        o.SaveTokens = true;
        o.GetClaimsFromUserInfoEndpoint = !string.IsNullOrWhiteSpace(auth.Oidc.UserInfoEndpoint) || !auth.Oidc.HasManualEndpoints;
        o.CallbackPath = auth.Oidc.CallbackPath;
        o.SignedOutCallbackPath = auth.Oidc.SignedOutCallbackPath;
        o.SignInScheme = IdentityConstants.ExternalScheme;

        o.Scope.Clear();
        foreach (var s in auth.Oidc.Scopes) o.Scope.Add(s);

        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

        o.Events.OnTokenValidated = async ctx =>
        {
            var sp = ctx.HttpContext.RequestServices;
            var prov = sp.GetRequiredService<UserProvisioningService>();
            var signInMgr = sp.GetRequiredService<SignInManager<AppUser>>();

            var user = await prov.ProvisionFromOidcAsync(ctx.Principal!);
            if (user is null || user.IsDisabled)
            {
                ctx.Fail("User not authorized for this application");
                return;
            }
            await signInMgr.SignInAsync(user, isPersistent: true);
            ctx.HandleResponse();
            ctx.Response.Redirect(ctx.Properties?.RedirectUri ?? "/");
        };
    });
}

builder.Services.AddAuthorization();

// ----- App services -----------------------------------------------------
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddScoped<UserProvisioningService>();
builder.Services.AddScoped<JobLogger>();
builder.Services.AddScoped<JobFinalizer>();
builder.Services.AddScoped<OpdsService>();
builder.Services.AddHttpClient<OpenAiClient>();
builder.Services.Configure<OpdsOptions>(builder.Configuration.GetSection("Opds"));
var opdsOptions = builder.Configuration.GetSection("Opds").Get<OpdsOptions>() ?? new OpdsOptions();
builder.Services.AddHttpClient("opds")
    .ConfigurePrimaryHttpMessageHandler(sp =>
        OpdsHttpHandler.Create(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("opds"),
            opdsOptions.AllowPrivateNetworks));
Directory.CreateDirectory(storage.KeysDirectory);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(storage.KeysDirectory));
builder.Services.AddHostedService<JobWorker>();

builder.Services.AddSignalR();
// No CORS by default — the SPA is served from the same origin as the API
// (Vite dev-server proxies `/api` and `/hubs` to this server). If you need
// to host the frontend on a different origin, configure an explicit
// allow-list here; never reflect arbitrary origins together with
// AllowCredentials, since that turns the browser same-origin policy off.

builder.Services.AddEndpointsApiExplorer();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = storage.MaxUploadBytes;
    o.ValueLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = storage.MaxUploadBytes);

// ----- Build & seed -----------------------------------------------------
var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Directory.CreateDirectory(storage.DataDirectory);
    if ((await db.Database.GetPendingMigrationsAsync()).Any())
        await db.Database.MigrateAsync();
    else
        await db.Database.EnsureCreatedAsync();
    await SchemaMigrator.ApplyAsync(db, scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>());
    await SeedData.EnsureRolesAsync(scope.ServiceProvider);
    if (auth.Password.Enabled
        && !string.IsNullOrWhiteSpace(auth.FirstAdminEmail)
        && !string.IsNullOrWhiteSpace(auth.FirstAdminPassword))
    {
        await SeedData.EnsureFirstAdminAsync(scope.ServiceProvider, auth);
    }
}

// MUST be first — runs before authentication so OIDC sees the correct scheme.
app.UseForwardedHeaders();

app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapSetupEndpoints();
app.MapAuthEndpoints();
app.MapSettingsEndpoints();
app.MapJobsEndpoints();
app.MapReviewsEndpoints();
app.MapUsersEndpoints();
app.MapOpdsEndpoints();
app.MapHub<JobHub>("/hubs/jobs");

// SPA fallback — anything that didn't match an endpoint serves index.html
app.MapFallbackToFile("/index.html");

app.Run();

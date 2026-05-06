using System.Security.Claims;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using NovelCleaner.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NovelCleaner.Server.Endpoints;

public static class OpdsEndpoints
{
    public static void MapOpdsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/opds").RequireAuthorization();

        // ----- Sources CRUD -----
        group.MapGet("/sources", async (HttpContext http, AppDbContext db) =>
        {
            var uid = UserId(http);
            var rows = await db.OpdsSources.Where(s => s.UserId == uid)
                .OrderBy(s => s.Name)
                .Select(s => new
                {
                    s.Id, s.Name, s.Url,
                    hasCredentials = !string.IsNullOrEmpty(s.Username),
                    s.AutoClean, s.CreatedAt, s.LastUsedAt,
                })
                .ToListAsync();
            return Results.Ok(rows);
        });

        group.MapPost("/sources", async (
            HttpContext http,
            AppDbContext db,
            OpdsService opds,
            [FromBody] SourceRequest req) =>
        {
            var uid = UserId(http);
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest(new { error = "name and url required" });
            if (!IsAcceptablePublicUri(req.Url, out var rejectReason))
                return Results.BadRequest(new { error = rejectReason });

            var src = new OpdsSource
            {
                UserId = uid,
                Name = req.Name.Trim(),
                Url = req.Url.Trim(),
                Username = string.IsNullOrWhiteSpace(req.Username) ? null : req.Username,
                PasswordCipher = string.IsNullOrEmpty(req.Password) ? null : opds.Encrypt(req.Password),
                AutoClean = req.AutoClean,
            };
            db.OpdsSources.Add(src);
            await db.SaveChangesAsync();
            return Results.Ok(new { id = src.Id });
        });

        group.MapPut("/sources/{id:guid}", async (
            Guid id, HttpContext http, AppDbContext db, OpdsService opds,
            [FromBody] SourceRequest req) =>
        {
            var src = await GetOwnedAsync(db, http, id);
            if (src is null) return Results.NotFound();
            if (!IsAcceptablePublicUri(req.Url, out var rejectReason))
                return Results.BadRequest(new { error = rejectReason });
            src.Name = req.Name.Trim();
            src.Url = req.Url.Trim();
            src.AutoClean = req.AutoClean;
            if (req.Username is not null) src.Username = string.IsNullOrWhiteSpace(req.Username) ? null : req.Username;
            if (req.Password is not null) src.PasswordCipher = string.IsNullOrEmpty(req.Password) ? null : opds.Encrypt(req.Password);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapDelete("/sources/{id:guid}", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var src = await GetOwnedAsync(db, http, id);
            if (src is null) return Results.NotFound();
            db.OpdsSources.Remove(src);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ----- Browse -----
        group.MapGet("/sources/{id:guid}/browse", async (
            Guid id, HttpContext http, AppDbContext db, OpdsService opds,
            ILoggerFactory loggerFactory,
            [FromQuery] string? url, CancellationToken ct) =>
        {
            var src = await GetOwnedAsync(db, http, id);
            if (src is null) return Results.NotFound();
            var target = string.IsNullOrWhiteSpace(url) ? src.Url : url;
            if (!IsSameOriginAsSource(target, src.Url, out var reason))
                return Results.BadRequest(new { error = reason });

            var log = loggerFactory.CreateLogger("opds.browse");
            try
            {
                var feed = await opds.FetchAsync(src, target, ct);
                return Results.Ok(new
                {
                    title = feed.Title,
                    navigationLinks = feed.NavigationLinks.Select(l => new
                        { l.Href, l.Rel, l.Type, l.Title }),
                    entries = feed.Entries.Select(e => new
                    {
                        title = e.Title,
                        author = e.Author,
                        summary = e.Summary,
                        coverHref = e.CoverHref,
                        acquisitionLinks = e.AcquisitionLinks.Select(l => new
                            { l.Href, l.Rel, l.Type, l.Title }),
                    }),
                });
            }
            catch (Exception ex)
            {
                // Don't echo the exception message — it can leak parsed bytes
                // / internal hostnames when the SSRF target returns garbage.
                log.LogWarning(ex, "OPDS browse failed for source {SourceId}", id);
                return Results.BadRequest(new { error = "OPDS request failed." });
            }
        });

        // ----- Import (download + queue cleaning) -----
        group.MapPost("/sources/{id:guid}/import", async (
            Guid id, HttpContext http, AppDbContext db, OpdsService opds,
            JobQueue queue, [FromBody] ImportRequest req, CancellationToken ct) =>
        {
            var uid = UserId(http);
            var src = await GetOwnedAsync(db, http, id);
            if (src is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Href)) return Results.BadRequest(new { error = "href required" });
            if (!IsSameOriginAsSource(req.Href, src.Url, out var reason))
                return Results.BadRequest(new { error = reason });

            var appS = await db.AppSettings.FirstOrDefaultAsync(x => x.Id == AppSettings.SingletonKey, ct);
            if (appS is null || string.IsNullOrWhiteSpace(appS.Model))
                return Results.BadRequest(new { error = "Global LLM settings have not been configured by an admin yet." });
            var userS = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == uid, ct);
            if (userS is null)
            {
                userS = new UserSettings { UserId = uid };
                db.UserSettings.Add(userS);
                await db.SaveChangesAsync(ct);
            }

            var (path, name, size) = await opds.DownloadEntryAsync(src, req.Href, ct);

            var job = new CleanJob
            {
                UserId = uid,
                OriginalFileName = name,
                FileSizeBytes = size,
                InputStoragePath = path,
                // Per-user
                ScanAll = userS.ScanAll,
                PatternsJson = userS.PatternsJson,
                ContextWindow = userS.ContextWindow,
                // Admin-managed
                MaxWorkers = appS.MaxWorkers,
                Model = appS.Model,
            };
            db.CleanJobs.Add(job);
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(job.Id, ct);
            return Results.Ok(new { jobId = job.Id });
        });
    }

    private static Guid UserId(HttpContext http)
        => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static Task<OpdsSource?> GetOwnedAsync(AppDbContext db, HttpContext http, Guid id)
    {
        var uid = UserId(http);
        return db.OpdsSources.FirstOrDefaultAsync(s => s.Id == id && s.UserId == uid);
    }

    public sealed record SourceRequest(string Name, string Url, string? Username, string? Password, bool AutoClean);
    public sealed record ImportRequest(string Href, string? Title);

    /// <summary>
    /// Returns true iff the URL is well-formed, absolute, http(s), and not a
    /// literal private/loopback/link-local address. Connect-time defence in
    /// <see cref="OpdsHttpHandler"/> handles DNS-rebind cases; this is the
    /// fast-fail guard at the request boundary.
    /// </summary>
    private static bool IsAcceptablePublicUri(string? raw, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw, UriKind.Absolute, out var u))
        {
            error = "URL must be absolute.";
            return false;
        }
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only http and https URLs are allowed.";
            return false;
        }
        // Reject literal private/loopback IPs at the boundary; ConnectCallback
        // covers DNS-resolved cases.
        if (System.Net.IPAddress.TryParse(u.Host, out var ip)
            && (System.Net.IPAddress.IsLoopback(ip)
                || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
                || IsPrivateIPv4(ip)))
        {
            error = "URL points at a non-public address.";
            return false;
        }
        return true;
    }

    private static bool IsPrivateIPv4(System.Net.IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 0
            || b[0] == 127;
    }

    /// <summary>
    /// Confirms that <paramref name="target"/> has the same scheme + host +
    /// port as the source's stored URL, so a user can't redirect the server
    /// (with attached credentials) to an arbitrary host via browse/import.
    /// </summary>
    private static bool IsSameOriginAsSource(string target, string sourceUrl, out string error)
    {
        error = "";
        if (!IsAcceptablePublicUri(target, out error)) return false;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var s)
            || !Uri.TryCreate(target, UriKind.Absolute, out var t))
        {
            error = "Could not parse URLs.";
            return false;
        }
        if (!string.Equals(s.Scheme, t.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(s.Host,   t.Host,   StringComparison.OrdinalIgnoreCase)
            || s.Port != t.Port)
        {
            error = "URL must be on the same origin as the configured source.";
            return false;
        }
        return true;
    }
}

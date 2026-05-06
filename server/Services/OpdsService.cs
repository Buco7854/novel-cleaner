using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using NovelCleaner.Server.Configuration;
using NovelCleaner.Server.Data;
using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NovelCleaner.Server.Services;

public sealed record OpdsLink(string Href, string? Rel, string? Type, string? Title);
public sealed record OpdsEntry(
    string Title,
    string? Author,
    string? Summary,
    string? CoverHref,
    IReadOnlyList<OpdsLink> AcquisitionLinks);
public sealed record OpdsFeed(
    string? Title,
    IReadOnlyList<OpdsLink> NavigationLinks,
    IReadOnlyList<OpdsEntry> Entries);

public sealed class OpdsService(
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dp,
    AppDbContext db,
    IOptions<StorageOptions> storage,
    ILogger<OpdsService> log)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private const string AcqRel = "http://opds-spec.org/acquisition";
    private const string ImageRel = "http://opds-spec.org/image";

    private readonly IDataProtector _protector = dp.CreateProtector("opds.credentials.v1");

    public string? Encrypt(string? plain) => plain is null ? null : _protector.Protect(plain);
    public string? Decrypt(string? cipher) => cipher is null ? null : _protector.Unprotect(cipher);

    public async Task<OpdsFeed> FetchAsync(OpdsSource source, string url, CancellationToken ct)
    {
        using var http = BuildClient(source);
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(ct);
        return Parse(xml, baseUri: new Uri(url));
    }

    public async Task<(string Path, string FileName, long Size)> DownloadEntryAsync(
        OpdsSource source, string href, CancellationToken ct)
    {
        using var http = BuildClient(source);
        using var resp = await http.GetAsync(href, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName
            ?? Path.GetFileName(new Uri(href).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
            fileName = $"book-{Guid.NewGuid():N}.epub";
        fileName = fileName.Trim('"');

        Directory.CreateDirectory(storage.Value.UploadDirectory);
        var path = Path.Combine(storage.Value.UploadDirectory, $"opds_{Guid.NewGuid():N}_{Path.GetFileName(fileName)}");

        await using var fs = File.Create(path);
        await resp.Content.CopyToAsync(fs, ct);
        var size = new FileInfo(path).Length;
        source.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (path, Path.GetFileName(fileName), size);
    }

    private HttpClient BuildClient(OpdsSource source)
    {
        var client = httpFactory.CreateClient("opds");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NovelCleaner/1.0");
        if (!string.IsNullOrEmpty(source.Username))
        {
            var pwd = Decrypt(source.PasswordCipher) ?? "";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.Username}:{pwd}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return client;
    }

    private static OpdsFeed Parse(string xml, Uri baseUri)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("OPDS feed parse error: " + ex.Message);
        }

        var feed = doc.Root!;
        var title = feed.Element(Atom + "title")?.Value;

        var navLinks = feed.Elements(Atom + "link")
            .Select(l => new OpdsLink(
                Resolve(baseUri, l.Attribute("href")?.Value ?? ""),
                l.Attribute("rel")?.Value,
                l.Attribute("type")?.Value,
                l.Attribute("title")?.Value))
            .Where(l => !string.IsNullOrWhiteSpace(l.Href))
            .ToArray();

        var entries = feed.Elements(Atom + "entry").Select(e =>
        {
            var t = e.Element(Atom + "title")?.Value ?? "(untitled)";
            var author = e.Element(Atom + "author")?.Element(Atom + "name")?.Value;
            var summary = e.Element(Atom + "summary")?.Value
                ?? e.Element(Atom + "content")?.Value;

            var allLinks = e.Elements(Atom + "link")
                .Select(l => new OpdsLink(
                    Resolve(baseUri, l.Attribute("href")?.Value ?? ""),
                    l.Attribute("rel")?.Value,
                    l.Attribute("type")?.Value,
                    l.Attribute("title")?.Value))
                .Where(l => !string.IsNullOrWhiteSpace(l.Href))
                .ToList();

            var cover = allLinks.FirstOrDefault(l => l.Rel == ImageRel || l.Rel == "http://opds-spec.org/image/thumbnail")?.Href;
            var acq = allLinks.Where(l => l.Rel != null && l.Rel.StartsWith(AcqRel)).ToArray();

            return new OpdsEntry(t, author, summary, cover, acq);
        }).ToArray();

        return new OpdsFeed(title, navLinks, entries);
    }

    private static string Resolve(Uri baseUri, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";
        return Uri.TryCreate(baseUri, href, out var abs) ? abs.ToString() : href;
    }
}

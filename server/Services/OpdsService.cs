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

/// <summary>A folder-like entry that links to another OPDS feed.</summary>
public sealed record OpdsCategoryEntry(string Title, string? Summary, string Href);

/// <summary>A real book entry with at least one acquisition link.</summary>
public sealed record OpdsBookEntry(
    string Title,
    string? Author,
    string? Summary,
    /// <summary>True when <see cref="Summary"/> is HTML (atom:content type=html).
    /// Lets the client safely render markup vs. plaintext.</summary>
    bool SummaryIsHtml,
    string? CoverHref,
    /// <summary>Subject categories / genres pulled from atom:category labels.</summary>
    IReadOnlyList<string> Categories,
    /// <summary>Languages pulled from dc:language elements.</summary>
    IReadOnlyList<string> Languages,
    /// <summary>Publisher (dcterms:publisher / dc:publisher) when present.</summary>
    string? Publisher,
    /// <summary>Publication date (dcterms:issued / atom:published) when present.</summary>
    string? Issued,
    IReadOnlyList<OpdsLink> AcquisitionLinks);

public sealed record OpdsFeed(
    string? Title,
    IReadOnlyList<OpdsLink> NavigationLinks,
    IReadOnlyList<OpdsCategoryEntry> Categories,
    IReadOnlyList<OpdsBookEntry> Books);

public sealed class OpdsService(
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dp,
    AppDbContext db,
    IOptions<StorageOptions> storage,
    ILogger<OpdsService> log)
{
    private static readonly XNamespace Atom     = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc       = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms  = "http://purl.org/dc/terms/";
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

    /// <summary>
    /// Streams an asset (typically a cover image) from the OPDS source to
    /// <paramref name="outResp"/>, attaching the source's stored credentials.
    /// Used by the cover-image proxy so the browser never talks to the
    /// upstream directly — otherwise a private OPDS catalog returns
    /// <c>401 WWW-Authenticate: Basic</c> and the browser raises its native
    /// auth prompt for the upstream host.
    /// </summary>
    public async Task ProxyAssetAsync(
        OpdsSource source, string url, HttpResponse outResp, CancellationToken ct)
    {
        using var http = BuildClient(source);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Never forward upstream auth-challenge headers — that would
            // re-trigger the browser auth prompt this proxy exists to avoid.
            outResp.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        outResp.StatusCode = StatusCodes.Status200OK;
        outResp.ContentType = resp.Content.Headers.ContentType?.ToString()
            ?? "application/octet-stream";
        if (resp.Content.Headers.ContentLength is { } len)
            outResp.ContentLength = len;
        outResp.Headers.CacheControl = "private, max-age=3600";
        await resp.Content.CopyToAsync(outResp.Body, ct);
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

        var categories = new List<OpdsCategoryEntry>();
        var books = new List<OpdsBookEntry>();

        foreach (var e in feed.Elements(Atom + "entry"))
        {
            var t = e.Element(Atom + "title")?.Value?.Trim() ?? "(untitled)";
            var author = e.Element(Atom + "author")?.Element(Atom + "name")?.Value?.Trim();

            // Pick the richer of summary/content. Track whether we picked
            // an HTML element so the client can render markup safely
            // instead of dumping <p> tags as text.
            var contentEl = e.Element(Atom + "content");
            var summaryEl = e.Element(Atom + "summary");
            string? summary = null;
            var summaryIsHtml = false;
            if (contentEl is not null && !string.IsNullOrWhiteSpace(contentEl.Value))
            {
                summary = contentEl.Value.Trim();
                var ctype = contentEl.Attribute("type")?.Value;
                summaryIsHtml = ctype is "html" or "xhtml";
            }
            else if (summaryEl is not null && !string.IsNullOrWhiteSpace(summaryEl.Value))
            {
                summary = summaryEl.Value.Trim();
                var stype = summaryEl.Attribute("type")?.Value;
                summaryIsHtml = stype is "html" or "xhtml";
            }

            // dc:* / dcterms:* metadata. Some catalogs put these as direct
            // children of <entry>, others namespace them with dcterms:.
            var entryCategories = e.Elements(Atom + "category")
                .Select(c => (c.Attribute("label")?.Value ?? c.Attribute("term")?.Value)?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var languages = e.Elements(Dc + "language")
                .Concat(e.Elements(DcTerms + "language"))
                .Select(x => x.Value.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var publisher = (e.Element(Dc + "publisher") ?? e.Element(DcTerms + "publisher"))?.Value?.Trim();
            var issued = (e.Element(DcTerms + "issued")
                ?? e.Element(Atom + "published"))?.Value?.Trim();

            var allLinks = e.Elements(Atom + "link")
                .Select(l => new OpdsLink(
                    Resolve(baseUri, l.Attribute("href")?.Value ?? ""),
                    l.Attribute("rel")?.Value,
                    l.Attribute("type")?.Value,
                    l.Attribute("title")?.Value))
                .Where(l => !string.IsNullOrWhiteSpace(l.Href))
                .ToList();

            var acq = allLinks.Where(l => l.Rel != null && l.Rel.StartsWith(AcqRel)).ToArray();

            // A book entry has at least one acquisition link. Anything else is
            // treated as a navigation/category entry (e.g. "By Newest",
            // "Authors", "Tags" in a Calibre catalog) and rendered as a folder.
            if (acq.Length > 0)
            {
                var cover = allLinks.FirstOrDefault(l =>
                    l.Rel == ImageRel || l.Rel == "http://opds-spec.org/image/thumbnail")?.Href;
                books.Add(new OpdsBookEntry(
                    t, author, summary, summaryIsHtml, cover,
                    entryCategories, languages, publisher, issued, acq));
            }
            else
            {
                // Pick the link that points at another OPDS feed: prefer
                // rel="subsection", fall back to any link advertising an
                // atom+xml type, finally any non-self link.
                var navHref =
                    allLinks.FirstOrDefault(l => l.Rel == "subsection")?.Href
                    ?? allLinks.FirstOrDefault(l => l.Type?.Contains("atom+xml") == true)?.Href
                    ?? allLinks.FirstOrDefault(l => l.Rel != "self")?.Href;
                if (!string.IsNullOrWhiteSpace(navHref))
                    categories.Add(new OpdsCategoryEntry(t, summary, navHref));
            }
        }

        return new OpdsFeed(title, navLinks, categories, books);
    }

    private static string Resolve(Uri baseUri, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";
        return Uri.TryCreate(baseUri, href, out var abs) ? abs.ToString() : href;
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Tergeo.Server.Configuration;
using Tergeo.Server.Data;
using Tergeo.Server.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Tergeo.Server.Services;

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
    /// <summary>Series name from Calibre (<c>calibre:series</c>) or
    /// schema.org (<c>schema:Series</c>) when present.</summary>
    string? Series,
    /// <summary>Position within the series. Kept as a string so non-integer
    /// indices like Calibre's "1.5" survive verbatim.</summary>
    string? SeriesIndex,
    IReadOnlyList<OpdsLink> AcquisitionLinks);

public sealed record OpdsFeed(
    string? Title,
    IReadOnlyList<OpdsLink> NavigationLinks,
    IReadOnlyList<OpdsCategoryEntry> Categories,
    IReadOnlyList<OpdsBookEntry> Books,
    /// <summary>
    /// Resolved OPDS search-URL template. Contains a literal
    /// <c>{searchTerms}</c> placeholder the client substitutes before
    /// navigating. Null when the catalog doesn't advertise search.
    /// </summary>
    string? SearchTemplate);

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
    private static readonly XNamespace Calibre  = "http://calibre-ebook.com/2009/metadata";
    private static readonly XNamespace Schema   = "http://schema.org/";
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
        var baseUri = new Uri(url);
        var (parsed, searchHref) = Parse(xml, baseUri);

        // OPDS catalogs advertise search via a `link rel="search"` whose
        // href usually points at an OpenSearch description document, not the
        // search endpoint itself. Resolve the description here so the
        // frontend gets a ready-to-substitute template back.
        var searchTemplate = searchHref is null
            ? null
            : await ResolveSearchTemplateAsync(http, searchHref, baseUri, ct);

        return parsed with { SearchTemplate = searchTemplate };
    }

    /// <summary>
    /// Two link shapes seen in the wild for the search link:
    ///   (a) href points at an OpenSearch description doc — fetch it, pull
    ///       the `application/atom+xml` Url template out of it
    ///   (b) href points at the search endpoint directly with the OpenSearch
    ///       template embedded (e.g. <c>/search?q={searchTerms}</c>)
    /// We try (a) first; on parse failure or no usable Url element, fall
    /// back to (b) and assume the href IS the template.
    /// </summary>
    private static async Task<string?> ResolveSearchTemplateAsync(
        HttpClient http, string searchHref, Uri baseUri, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(searchHref, ct);
            if (!resp.IsSuccessStatusCode) return FallbackTemplate(searchHref, baseUri);
            var body = await resp.Content.ReadAsStringAsync(ct);
            // OpenSearch description: <OpenSearchDescription><Url type="..." template="..."/></OpenSearchDescription>
            // Pick the Url whose type is an OPDS atom feed; fall back to the
            // first Url with a template if none are explicitly atom-typed.
            var doc = XDocument.Parse(body);
            var urls = doc.Descendants()
                .Where(x => x.Name.LocalName == "Url"
                    && !string.IsNullOrEmpty(x.Attribute("template")?.Value))
                .ToList();
            if (urls.Count == 0) return FallbackTemplate(searchHref, baseUri);
            var atomUrl = urls.FirstOrDefault(u =>
                (u.Attribute("type")?.Value ?? "").Contains("atom+xml", StringComparison.OrdinalIgnoreCase))
                ?? urls[0];
            var template = atomUrl.Attribute("template")!.Value.Trim();
            return Resolve(baseUri, template);
        }
        catch
        {
            return FallbackTemplate(searchHref, baseUri);
        }

        static string? FallbackTemplate(string href, Uri baseUri)
            => href.Contains("{searchTerms}", StringComparison.Ordinal)
                ? Resolve(baseUri, href)
                : null;
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tergeo/1.0");
        if (!string.IsNullOrEmpty(source.Username))
        {
            var pwd = Decrypt(source.PasswordCipher) ?? "";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.Username}:{pwd}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return client;
    }

    private static (OpdsFeed Feed, string? SearchHref) Parse(string xml, Uri baseUri)
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

        // OpenSearch search link. Some catalogs use rel="search"; some
        // namespace it. Prefer the OpenSearch-typed one when multiple are
        // present (Calibre, for instance, advertises both rel="search" and
        // rel="self" on the same href).
        var searchHref = navLinks.FirstOrDefault(l =>
            l.Rel == "search"
            && (l.Type ?? "").Contains("opensearchdescription", StringComparison.OrdinalIgnoreCase))?.Href
            ?? navLinks.FirstOrDefault(l => l.Rel == "search")?.Href;

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

            // Series + index. Two flavours seen in the wild:
            //   1. Calibre (most common): <calibre:series>Name</calibre:series>
            //      and <calibre:series_index>3.0</calibre:series_index>
            //   2. schema.org: <schema:Series name="Name"><schema:position>3</schema:position></schema:Series>
            //      (sometimes <schema:BookSeries> instead of <schema:Series>)
            // Calibre wins when both are present — its index keeps the user's
            // chosen precision (1.5, 2a, etc.) verbatim.
            var (series, seriesIndex) = ReadSeries(e);

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
                    entryCategories, languages, publisher, issued,
                    series, seriesIndex, acq));
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

        // SearchTemplate is filled in by FetchAsync after resolving the
        // OpenSearch description; pass null here.
        return (new OpdsFeed(title, navLinks, categories, books, null), searchHref);
    }

    private static string Resolve(Uri baseUri, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";
        return Uri.TryCreate(baseUri, href, out var abs) ? abs.ToString() : href;
    }

    /// <summary>
    /// Pulls series name + position from an OPDS entry. Three flavours
    /// supported, in priority order:
    ///   1. Calibre namespace (Calibre / Calibre-Web) — most common.
    ///   2. EPUB 3 standard metadata as Grimmory ships it: a
    ///      <c>&lt;meta property="belongs-to-collection" id="..."&gt;</c>
    ///      element with a refining
    ///      <c>&lt;meta property="group-position" refines="#id"&gt;</c>.
    ///   3. schema.org <c>Series</c> / <c>BookSeries</c>.
    /// Returns nulls when nothing usable is present.
    /// </summary>
    private static (string? Name, string? Index) ReadSeries(XElement entry)
    {
        var calibreName  = entry.Element(Calibre + "series")?.Value?.Trim();
        var calibreIndex = entry.Element(Calibre + "series_index")?.Value?.Trim();
        if (!string.IsNullOrEmpty(calibreName))
            return (calibreName, NormalizeIndex(calibreIndex));

        // EPUB 3 form. The metas don't carry a stable namespace prefix in
        // the wild — match by local-name + the property attribute.
        var collection = entry.Elements()
            .FirstOrDefault(x => x.Name.LocalName == "meta"
                && x.Attribute("property")?.Value == "belongs-to-collection");
        if (collection is not null)
        {
            var name = collection.Value?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var collectionId = collection.Attribute("id")?.Value;
                // The refining position must point back at this collection
                // by id. When several collections nest, the refines target
                // disambiguates which one the position applies to.
                var position = entry.Elements()
                    .Where(x => x.Name.LocalName == "meta"
                        && x.Attribute("property")?.Value == "group-position")
                    .Select(x => new
                    {
                        Refines = x.Attribute("refines")?.Value?.TrimStart('#'),
                        Value = x.Value?.Trim(),
                    })
                    .FirstOrDefault(p => collectionId is null
                        || string.Equals(p.Refines, collectionId, StringComparison.Ordinal));
                return (name, NormalizeIndex(position?.Value));
            }
        }

        // schema.org variants. The series element carries the name either as
        // an attribute (`name="..."`) or as a `<schema:name>` child; the
        // position is a child element. We accept both Series and BookSeries.
        var schemaSeries = entry.Element(Schema + "Series")
                        ?? entry.Element(Schema + "BookSeries");
        if (schemaSeries is not null)
        {
            var schemaName = schemaSeries.Attribute("name")?.Value?.Trim()
                          ?? schemaSeries.Element(Schema + "name")?.Value?.Trim();
            var schemaPos = schemaSeries.Element(Schema + "position")?.Value?.Trim();
            if (!string.IsNullOrEmpty(schemaName))
                return (schemaName, NormalizeIndex(schemaPos));
        }

        return (null, null);
    }

    /// <summary>
    /// Trims a series-index string and drops trailing <c>.0</c> so Calibre's
    /// canonical "3.0" renders as "3" while non-integer values like "1.5"
    /// pass through unchanged.
    /// </summary>
    private static string? NormalizeIndex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.EndsWith(".0", StringComparison.Ordinal))
            trimmed = trimmed[..^2];
        return trimmed.Length == 0 ? null : trimmed;
    }
}

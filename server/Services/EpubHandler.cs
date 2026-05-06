using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;

namespace NovelCleaner.Server.Services;

public sealed record EpubDocument(string Name, byte[] Content);
public sealed record RemovalInstruction(string Remove, string Reason);
public sealed record AppliedRemoval(string Removed, string Reason);

/// <summary>Subset of EPUB OPF metadata we surface for display and editing.
/// All fields are optional — sparse EPUBs and older catalogs may omit any of them.</summary>
public sealed record EpubMetadata(
    string? Title,
    string? Author,
    string? Language,
    string? Publisher,
    string? Description);

public static class EpubHandler
{
    private static readonly Regex EmptyTags = new(
        "<(p|div|span|em|strong|a)\\b[^>]*>\\s*</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNs  = "http://purl.org/dc/elements/1.1/";

    /// <summary>
    /// Reads the OPF metadata block. Returns an empty <see cref="EpubMetadata"/>
    /// when the EPUB has no OPF or the OPF is malformed — callers should treat
    /// every field as optional.
    /// </summary>
    public static EpubMetadata ReadMetadata(string epubPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(epubPath);
            var opfPath = TryFindOpfPath(archive);
            if (opfPath is null) return new EpubMetadata(null, null, null, null, null);

            var opfEntry = archive.GetEntry(opfPath);
            if (opfEntry is null) return new EpubMetadata(null, null, null, null, null);

            using var s = opfEntry.Open();
            var doc = XDocument.Load(s);
            var metadata = doc.Descendants(OpfNs + "metadata").FirstOrDefault()
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            if (metadata is null) return new EpubMetadata(null, null, null, null, null);

            string? Read(string name)
                => Trim(metadata.Elements(DcNs + name).FirstOrDefault()?.Value
                    ?? metadata.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value);

            return new EpubMetadata(
                Title:       Read("title"),
                Author:      Read("creator"),
                Language:    Read("language"),
                Publisher:   Read("publisher"),
                Description: Read("description"));
        }
        catch
        {
            return new EpubMetadata(null, null, null, null, null);
        }
    }

    /// <summary>
    /// Writes <paramref name="metadata"/> back into the EPUB at
    /// <paramref name="epubPath"/>. Updates the existing dc:* elements when
    /// present, creates them when missing, and removes them when the
    /// caller explicitly nulls a field. No-ops on EPUBs without a parseable OPF.
    /// </summary>
    public static void WriteMetadata(string epubPath, EpubMetadata metadata)
    {
        using var archive = ZipFile.Open(epubPath, ZipArchiveMode.Update);
        var opfPath = TryFindOpfPath(archive);
        if (opfPath is null) return;

        var opfEntry = archive.GetEntry(opfPath);
        if (opfEntry is null) return;

        XDocument doc;
        using (var s = opfEntry.Open())
        {
            doc = XDocument.Load(s, LoadOptions.PreserveWhitespace);
        }

        var metaEl = doc.Descendants(OpfNs + "metadata").FirstOrDefault()
            ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metaEl is null) return;

        // dc namespace might be declared on <package> or on <metadata> — preserve
        // whichever the source uses by reusing the existing elements rather than
        // forcing a new namespace prefix.
        UpsertDc(metaEl, "title",       metadata.Title);
        UpsertDc(metaEl, "creator",     metadata.Author);
        UpsertDc(metaEl, "language",    metadata.Language);
        UpsertDc(metaEl, "publisher",   metadata.Publisher);
        UpsertDc(metaEl, "description", metadata.Description);

        // ZipArchive.Update with an existing entry: delete + recreate so the
        // new content replaces the old. Writing through the existing Open()
        // stream truncates only when the new content is the same length or
        // shorter, which isn't reliable here.
        opfEntry.Delete();
        var rewritten = archive.CreateEntry(opfPath, CompressionLevel.Optimal);
        using var os = rewritten.Open();
        doc.Save(os, SaveOptions.DisableFormatting);
    }

    private static void UpsertDc(XElement metadata, string localName, string? value)
    {
        var existing = metadata.Elements(DcNs + localName)
            .Concat(metadata.Elements().Where(x => x.Name.LocalName == localName))
            .ToList();

        if (string.IsNullOrWhiteSpace(value))
        {
            foreach (var existingEl in existing) existingEl.Remove();
            return;
        }

        if (existing.Count > 0)
        {
            existing[0].Value = value.Trim();
            // Drop duplicates so we don't end up with two <dc:title> elements.
            for (var i = 1; i < existing.Count; i++) existing[i].Remove();
            return;
        }

        // No element existed — create one in the dc namespace; XLinq emits
        // the right xmlns: prefix automatically based on what's declared up
        // the tree, so we just append a fresh element with the local name.
        metadata.Add(new XElement(DcNs + localName, value.Trim()));
    }

    private static string? Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? TryFindOpfPath(ZipArchive archive)
    {
        var container = archive.GetEntry("META-INF/container.xml");
        if (container is null) return null;
        try
        {
            using var s = container.Open();
            var doc = XDocument.Load(s);
            XNamespace ns = "urn:oasis:names:tc:opendocument:xmlns:container";
            var path = doc.Descendants(ns + "rootfile").FirstOrDefault()
                ?.Attribute("full-path")?.Value;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Streams a single entry out of the EPUB at <paramref name="epubPath"/>.
    /// Used by the preview iframe's asset proxy so chapter HTML can reach
    /// its companion CSS / fonts / images via relative URLs. Returns null
    /// when the entry doesn't exist; throws on traversal-style paths.
    /// </summary>
    public static (byte[] Bytes, string ContentType)? ReadEntry(string epubPath, string entryPath)
    {
        // Reject absolute paths and any traversal segments. The OPF can
        // legitimately reference deeply-nested files, but never above the
        // archive root, so a literal ".." segment is always wrong.
        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0) return null;
        foreach (var seg in normalized.Split('/'))
            if (seg == "..") return null;

        using var archive = ZipFile.OpenRead(epubPath);
        var entry = archive.GetEntry(normalized)
            // Some EPUBs URL-encode hrefs in the OPF/spine even though the
            // ZIP entry is stored decoded ("Text%20Files/ch1.xhtml"). Try the
            // decoded form before giving up.
            ?? archive.GetEntry(Uri.UnescapeDataString(normalized));
        if (entry is null) return null;

        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return (ms.ToArray(), GuessContentType(normalized));
    }

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".xhtml"          => "application/xhtml+xml; charset=utf-8",
            ".xml" or ".opf" or ".ncx" => "application/xml; charset=utf-8",
            ".css"            => "text/css; charset=utf-8",
            ".js"             => "application/javascript; charset=utf-8",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            ".svg"            => "image/svg+xml",
            ".ttf"            => "font/ttf",
            ".otf"            => "font/otf",
            ".woff"           => "font/woff",
            ".woff2"          => "font/woff2",
            ".eot"            => "application/vnd.ms-fontobject",
            _                 => "application/octet-stream",
        };
    }

    public static IReadOnlyList<EpubDocument> ReadHtmlDocuments(string epubPath)
    {
        using var archive = ZipFile.OpenRead(epubPath);

        // Pull every HTML entry first, keyed by its full path so the spine
        // walk can resolve hrefs against it.
        var byPath = new Dictionary<string, EpubDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!IsHtmlEntry(entry.FullName)) continue;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byPath[entry.FullName] = new EpubDocument(entry.FullName, ms.ToArray());
        }

        // Authoritative reading order lives in the OPF spine — try that first.
        var spineOrder = TryReadSpineOrder(archive);
        if (spineOrder is { Count: > 0 })
        {
            var ordered = new List<EpubDocument>(byPath.Count);
            foreach (var path in spineOrder)
            {
                if (byPath.TryGetValue(path, out var doc))
                {
                    ordered.Add(doc);
                    byPath.Remove(path);
                }
            }
            // Append any HTML the spine didn't reference (cover variants, stray
            // notes) in natural order so they're still visible in the editor.
            ordered.AddRange(byPath.Values.OrderBy(d => d.Name, NaturalComparer.Instance));
            return ordered;
        }

        // Fallback: numeric-aware sort of the ZIP entry paths. "chapter2"
        // beats "chapter10" lexically, so plain ordinal would mis-order.
        var list = byPath.Values.ToList();
        list.Sort((a, b) => NaturalCompare(a.Name, b.Name));
        return list;
    }

    /// <summary>
    /// Walks <c>META-INF/container.xml</c> → OPF → <c>&lt;spine&gt;</c> to
    /// recover the publisher's reading order. Returns the spine entries
    /// resolved to full archive paths (relative to the OPF directory),
    /// or null when the structure is missing/malformed — the caller should
    /// fall back to a sort-by-name heuristic.
    /// </summary>
    private static List<string>? TryReadSpineOrder(ZipArchive archive)
    {
        try
        {
            var container = archive.GetEntry("META-INF/container.xml");
            if (container is null) return null;

            string opfPath;
            using (var s = container.Open())
            {
                var doc = XDocument.Load(s);
                XNamespace ns = "urn:oasis:names:tc:opendocument:xmlns:container";
                opfPath = doc.Descendants(ns + "rootfile").FirstOrDefault()
                    ?.Attribute("full-path")?.Value ?? "";
            }
            if (string.IsNullOrWhiteSpace(opfPath)) return null;

            var opfEntry = archive.GetEntry(opfPath);
            if (opfEntry is null) return null;

            var opfDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
            var idToHref = new Dictionary<string, string>(StringComparer.Ordinal);
            var spineRefs = new List<string>();

            using (var s = opfEntry.Open())
            {
                var opf = XDocument.Load(s);
                // OPF uses a default namespace; pick it up from the root so
                // we work with both 2.x and 3.x packages without hard-coding.
                var ns = opf.Root?.GetDefaultNamespace() ?? XNamespace.None;

                foreach (var item in opf.Descendants(ns + "item"))
                {
                    var id = item.Attribute("id")?.Value;
                    var href = item.Attribute("href")?.Value;
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(href))
                        idToHref[id!] = href!;
                }
                foreach (var itemref in opf.Descendants(ns + "itemref"))
                {
                    var idref = itemref.Attribute("idref")?.Value;
                    if (!string.IsNullOrEmpty(idref) && idToHref.TryGetValue(idref!, out var href))
                        spineRefs.Add(href);
                }
            }

            var resolved = new List<string>(spineRefs.Count);
            foreach (var href in spineRefs)
            {
                var combined = string.IsNullOrEmpty(opfDir) ? href : opfDir + "/" + href;
                // Hrefs may be percent-encoded ("Text%20Files/ch1.xhtml").
                var decoded = Uri.UnescapeDataString(combined.Replace('\\', '/'));
                resolved.Add(decoded);
            }
            return resolved;
        }
        catch
        {
            return null;
        }
    }

    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        public int Compare(string? x, string? y) => NaturalCompare(x ?? "", y ?? "");
    }

    /// <summary>
    /// Ordinal comparison that treats runs of digits as numbers, so
    /// "chapter2" sorts before "chapter10". Falls back to plain ordinal for
    /// non-digit segments. Cheap, allocation-free, no regex.
    /// </summary>
    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var ca = a[i];
            var cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                // Skip leading zeros so "01" and "1" compare equal in length.
                while (i < a.Length && a[i] == '0') i++;
                while (j < b.Length && b[j] == '0') j++;
                int ai = i, bj = j;
                while (ai < a.Length && char.IsDigit(a[ai])) ai++;
                while (bj < b.Length && char.IsDigit(b[bj])) bj++;
                var lenA = ai - i;
                var lenB = bj - j;
                if (lenA != lenB) return lenA - lenB; // longer number = larger
                var span = a.AsSpan(i, lenA).CompareTo(b.AsSpan(j, lenB), StringComparison.Ordinal);
                if (span != 0) return span;
                i = ai; j = bj;
            }
            else
            {
                if (ca != cb) return ca - cb;
                i++; j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }

    public static string ExtractText(byte[] htmlBytes)
    {
        var html = Encoding.UTF8.GetString(htmlBytes);
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(html);
        foreach (var n in doc.QuerySelectorAll("script,style").ToList()) n.Remove();

        // Walk the DOM ourselves so paragraph structure survives — naive
        // textContent + whitespace-collapse fuses every chapter into a
        // single line, which is unreadable in the editor. Block elements
        // contribute a paragraph break (\n\n); <br> contributes a single
        // newline; inline elements just flow their text.
        var sb = new StringBuilder();
        var root = (INode?)doc.Body ?? doc.DocumentElement;
        if (root is not null) WalkExtract(root, sb);

        var raw = sb.ToString();
        // Tighten whitespace: collapse runs of inline space, strip space
        // around newlines, cap consecutive blank lines at one.
        raw = InlineSpaceRun.Replace(raw, " ");
        raw = SpaceAroundNewline.Replace(raw, "\n");
        raw = ManyNewlines.Replace(raw, "\n\n");
        return raw.Trim();
    }

    private static readonly Regex InlineSpaceRun     = new(@"[ \t ]+",      RegexOptions.Compiled);
    private static readonly Regex SpaceAroundNewline = new(@"[ \t ]*\n[ \t ]*", RegexOptions.Compiled);
    private static readonly Regex ManyNewlines       = new(@"\n{3,}",            RegexOptions.Compiled);

    /// <summary>
    /// HTML elements that should produce a paragraph break in the
    /// visible-text projection. Lowercase — compared case-insensitively.
    /// </summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "nav", "aside",
        "main", "blockquote", "pre", "li", "ol", "ul", "dl", "dt", "dd",
        "tr", "td", "th", "table", "thead", "tbody", "tfoot",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "figure", "figcaption", "hr", "address",
    };

    private static void WalkExtract(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText txt)
            {
                sb.Append(txt.Data);
                continue;
            }
            if (child is not IElement el) continue;

            var tag = el.TagName;
            if (string.Equals(tag, "BR", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append('\n');
                continue;
            }
            var isBlock = BlockTags.Contains(tag);
            if (isBlock) sb.Append("\n\n");
            WalkExtract(el, sb);
            if (isBlock) sb.Append("\n\n");
        }
    }

    public static (byte[] Content, IReadOnlyList<AppliedRemoval> Applied) ApplyRemovals(
        byte[] htmlBytes,
        IReadOnlyList<RemovalInstruction> instructions)
    {
        var html = Encoding.UTF8.GetString(htmlBytes);
        var applied = new List<AppliedRemoval>();

        foreach (var instr in instructions)
        {
            var needle = instr.Remove?.Trim();
            if (string.IsNullOrEmpty(needle)) continue;
            var hit = FindNeedleInHtml(html, needle);
            if (hit is null) continue;
            html = RemoveVisibleCharsInRange(html, hit.Value.Index, hit.Value.Index + hit.Value.Length);
            html = HealSpacingAt(html, hit.Value.Index);
            applied.Add(new AppliedRemoval(needle, instr.Reason));
        }

        html = EmptyTags.Replace(html, "");
        return (Encoding.UTF8.GetBytes(html), applied);
    }

    /// <summary>
    /// Locates <paramref name="needle"/> inside the HTML. Tries a strict
    /// verbatim substring match first; if that misses, falls back to a
    /// tag/whitespace/entity-tolerant match against the visible-text
    /// projection of the HTML (so e.g. needle <c>"Sign up &amp; start"</c>
    /// — what the LLM saw after entity decoding — still matches the raw
    /// HTML <c>"Sign up &amp;amp; start"</c>).
    /// </summary>
    private static (int Index, int Length)? FindNeedleInHtml(string html, string needle)
    {
        var verbatim = html.IndexOf(needle, StringComparison.Ordinal);
        if (verbatim >= 0) return (verbatim, needle.Length);

        var (visible, map) = StripTagsAndCollapseWs(html);
        var needleNorm = CollapseWs(needle);
        if (needleNorm.Length == 0) return null;

        var visIdx = visible.IndexOf(needleNorm, StringComparison.Ordinal);
        if (visIdx < 0) return null;

        var endProj = visIdx + needleNorm.Length;
        var startHtml = map[visIdx];
        // Use the next projection char's HTML position as the end-exclusive
        // marker. This works whether a single projection char came from one
        // raw char OR from a multi-char entity like &amp; — the gap is always
        // sized correctly.
        var endHtml = endProj < map.Length ? map[endProj] : html.Length;
        return (startHtml, endHtml - startHtml);
    }

    /// <summary>
    /// Returns the visible-text projection of <paramref name="html"/> together
    /// with a per-character map back to original HTML offsets, so a position
    /// found in the projection can be translated to a span in the original.
    /// Tags are skipped, whitespace runs collapse to a single space, and HTML
    /// entities (<c>&amp;amp;</c>, <c>&amp;nbsp;</c>, numeric refs, …) are
    /// decoded so the projection mirrors what AngleSharp's <c>TextContent</c>
    /// gave to the LLM.
    /// </summary>
    private static (string Visible, int[] Map) StripTagsAndCollapseWs(string html)
    {
        var sb = new StringBuilder(html.Length);
        var map = new List<int>(html.Length);
        var inTag = false;
        var prevWs = true; // collapse leading whitespace
        var i = 0;
        while (i < html.Length)
        {
            var c = html[i];
            if (c == '<') { inTag = true; i++; continue; }
            if (c == '>') { inTag = false; i++; continue; }
            if (inTag) { i++; continue; }

            // Try to decode an HTML entity. Cap the search at 15 chars; valid
            // named/numeric refs are short.
            if (c == '&')
            {
                var maxScan = Math.Min(15, html.Length - i - 1);
                var semi = maxScan > 0 ? html.IndexOf(';', i + 1, maxScan) : -1;
                if (semi > i)
                {
                    var entity = html.Substring(i, semi - i + 1);
                    var decoded = WebUtility.HtmlDecode(entity);
                    if (decoded != entity && decoded.Length > 0)
                    {
                        // Successfully decoded — emit each decoded char,
                        // mapping every one back to the entity's start in HTML
                        // so any sub-range located by IndexOf still resolves.
                        foreach (var dc in decoded)
                        {
                            if (char.IsWhiteSpace(dc))
                            {
                                if (!prevWs)
                                {
                                    sb.Append(' ');
                                    map.Add(i);
                                    prevWs = true;
                                }
                            }
                            else
                            {
                                sb.Append(dc);
                                map.Add(i);
                                prevWs = false;
                            }
                        }
                        i = semi + 1;
                        continue;
                    }
                }
                // Not a valid entity — fall through, treat & as a literal char.
            }

            if (char.IsWhiteSpace(c))
            {
                if (!prevWs)
                {
                    sb.Append(' ');
                    map.Add(i);
                    prevWs = true;
                }
                i++;
                continue;
            }
            sb.Append(c);
            map.Add(i);
            prevWs = false;
            i++;
        }
        return (sb.ToString(), map.ToArray());
    }

    /// <summary>
    /// Removes only the visible characters (and entity sequences) inside
    /// <c>[startHtml, endHtml)</c>, preserving any HTML tags that fall in the
    /// range. Without this, a needle that spans <c>&lt;/div&gt;&lt;br /&gt;&lt;div&gt;</c>
    /// would wipe out the structural separators too, collapsing multiple
    /// paragraphs into one. <see cref="EmptyTags"/> sweeps any orphaned
    /// empty pairs left behind.
    /// </summary>
    private static string RemoveVisibleCharsInRange(string html, int startHtml, int endHtml)
    {
        if (startHtml < 0) startHtml = 0;
        if (endHtml > html.Length) endHtml = html.Length;
        if (startHtml >= endHtml) return html;

        var sb = new StringBuilder(html.Length);
        sb.Append(html, 0, startHtml);

        var inTag = false;
        for (var i = startHtml; i < endHtml; i++)
        {
            var c = html[i];
            if (c == '<')
            {
                inTag = true;
                sb.Append(c);
            }
            else if (inTag)
            {
                sb.Append(c);
                if (c == '>') inTag = false;
            }
            // else: visible char (or entity byte) inside the matched span — drop it.
        }

        sb.Append(html, endHtml, html.Length - endHtml);
        return sb.ToString();
    }

    private static string CollapseWs(string s) =>
        Regex.Replace(s, "\\s+", " ").Trim();

    private static readonly char[] SentencePunct = ['.', ',', ';', ':', '!', '?'];

    /// <summary>
    /// Heals the two common spacing artifacts that surface when the LLM
    /// strips just the watermark token without absorbing surrounding spaces:
    ///  - <c>"sheer  size"</c> (double whitespace where one was before)
    ///  - <c>"continues  ."</c> (orphan space before sentence punctuation)
    /// Acts only at <paramref name="gap"/> — the position where the removal
    /// took place — so we don't touch unrelated formatting.
    /// </summary>
    private static string HealSpacingAt(string html, int gap)
    {
        if (gap <= 0 || gap >= html.Length) return html;
        var before = html[gap - 1];
        var after  = html[gap];

        if (char.IsWhiteSpace(before) && char.IsWhiteSpace(after))
            return html.Remove(gap, 1);

        if (char.IsWhiteSpace(before) && Array.IndexOf(SentencePunct, after) >= 0)
            return html.Remove(gap - 1, 1);

        return html;
    }

    /// <summary>
    /// Re-emits the chapter HTML with block-level tags on their own lines so
    /// the editor's textarea and diff view aren't a single 50-kB line. Used
    /// on every entry into the repo (upload, OPDS import, clone, reset, AI
    /// write-back) so storage stays consistently formatted. Idempotent.
    /// Falls back to the input string when AngleSharp can't parse it (very
    /// rare — malformed XHTML).
    /// </summary>
    public static string PrettyPrintHtml(string html)
    {
        try
        {
            var parser = new HtmlParser();
            using var doc = parser.ParseDocument(html);
            using var sw = new StringWriter();
            doc.ToHtml(sw, new PrettyMarkupFormatter());
            return sw.ToString();
        }
        catch
        {
            return html;
        }
    }

    public static string PrettyPrintHtml(byte[] htmlBytes)
        => PrettyPrintHtml(Encoding.UTF8.GetString(htmlBytes));

    /// <summary>
    /// Strips the inter-tag whitespace pretty-printing added back out so the
    /// exported EPUB doesn't ship with bloated indentation. Text-content
    /// whitespace inside elements is preserved (browsers collapse it for
    /// rendering, but we don't want to alter prose bytes).
    /// </summary>
    public static byte[] MinifyHtml(string prettyHtml)
    {
        try
        {
            var parser = new HtmlParser();
            using var doc = parser.ParseDocument(prettyHtml);
            using var sw = new StringWriter();
            doc.ToHtml(sw, new MinifyMarkupFormatter());
            return Encoding.UTF8.GetBytes(sw.ToString());
        }
        catch
        {
            return Encoding.UTF8.GetBytes(prettyHtml);
        }
    }

    public static void WriteUpdatedEpub(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, byte[]> updates)
    {
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Copy(sourcePath, destinationPath);

        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Update);
        foreach (var (name, bytes) in updates)
        {
            var entry = archive.GetEntry(name);
            entry?.Delete();
            entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(bytes, 0, bytes.Length);
        }
    }

    private static bool IsHtmlEntry(string name)
        => name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
}

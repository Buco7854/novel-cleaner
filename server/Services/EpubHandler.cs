using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace NovelCleaner.Server.Services;

public sealed record EpubDocument(string Name, byte[] Content);
public sealed record RemovalInstruction(string Remove, string Reason);
public sealed record AppliedRemoval(string Removed, string Reason);

public static class EpubHandler
{
    private static readonly Regex EmptyTags = new(
        "<(p|div|span|em|strong|a)\\b[^>]*>\\s*</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<EpubDocument> ReadHtmlDocuments(string epubPath)
    {
        using var archive = ZipFile.OpenRead(epubPath);
        var docs = new List<EpubDocument>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!IsHtmlEntry(entry.FullName)) continue;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            docs.Add(new EpubDocument(entry.FullName, ms.ToArray()));
        }
        return docs;
    }

    public static string ExtractText(byte[] htmlBytes)
    {
        var html = Encoding.UTF8.GetString(htmlBytes);
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(html);
        foreach (var n in doc.QuerySelectorAll("script,style").ToList()) n.Remove();
        var text = doc.Body?.TextContent ?? doc.DocumentElement.TextContent;
        return Regex.Replace(text, "\\s+", " ").Trim();
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

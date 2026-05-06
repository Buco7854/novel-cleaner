using System.IO.Compression;
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
            var idx = html.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) continue;
            html = html.Remove(idx, needle.Length);
            applied.Add(new AppliedRemoval(needle, instr.Reason));
        }

        html = EmptyTags.Replace(html, "");
        return (Encoding.UTF8.GetBytes(html), applied);
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

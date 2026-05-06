using System.Text.RegularExpressions;

namespace NovelCleaner.Server.Services;

public sealed record ScanHit(IReadOnlyList<string> MatchedPatterns, IReadOnlyList<string> Excerpts);

public static class EpubScanner
{
    public static ScanHit Scan(string text, IReadOnlyList<string> patterns, int contextChars)
    {
        var matched = new List<string>();
        var windows = new List<(int Start, int End)>();

        foreach (var pattern in patterns)
        {
            Regex? regex = null;
            try { regex = new Regex(pattern, RegexOptions.Compiled); }
            catch { regex = null; }

            var foundAny = false;

            if (regex is not null)
            {
                foreach (Match m in regex.Matches(text))
                {
                    if (m.Length == 0) continue;
                    windows.Add((
                        Math.Max(0, m.Index - contextChars),
                        Math.Min(text.Length, m.Index + m.Length + contextChars)));
                    foundAny = true;
                }
            }
            else
            {
                var idx = 0;
                while (true)
                {
                    idx = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;
                    windows.Add((
                        Math.Max(0, idx - contextChars),
                        Math.Min(text.Length, idx + pattern.Length + contextChars)));
                    foundAny = true;
                    idx += pattern.Length;
                }
            }

            if (foundAny) matched.Add(pattern);
        }

        return new ScanHit(matched, MergeWindows(text, windows));
    }

    private static IReadOnlyList<string> MergeWindows(string text, List<(int Start, int End)> windows)
    {
        if (windows.Count == 0) return [];
        windows.Sort((a, b) => a.Start != b.Start ? a.Start - b.Start : a.End - b.End);

        var merged = new List<(int Start, int End)> { windows[0] };
        for (var i = 1; i < windows.Count; i++)
        {
            var (s, e) = windows[i];
            var last = merged[^1];
            if (s <= last.End)
                merged[^1] = (last.Start, Math.Max(last.End, e));
            else
                merged.Add((s, e));
        }

        return merged.Select(w => text[w.Start..w.End].Trim()).ToArray();
    }
}

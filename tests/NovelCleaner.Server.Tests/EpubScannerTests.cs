using NovelCleaner.Server.Services;

namespace NovelCleaner.Server.Tests;

public sealed class EpubScannerTests
{
    [Fact]
    public void Scan_returns_no_matches_when_pattern_is_absent()
    {
        var hit = EpubScanner.Scan("Chapter one. The wind howled.", ["watermark"], contextChars: 10);

        Assert.Empty(hit.MatchedPatterns);
        Assert.Empty(hit.Excerpts);
    }

    [Fact]
    public void Scan_extracts_context_window_around_plain_text_match()
    {
        var text = "Lorem ipsum WATERMARK dolor sit amet, consectetur";
        var hit = EpubScanner.Scan(text, ["WATERMARK"], contextChars: 5);

        Assert.Single(hit.MatchedPatterns);
        Assert.Single(hit.Excerpts);
        Assert.Contains("WATERMARK", hit.Excerpts[0]);
        // Excerpt is trimmed and limited to ±5 chars; should NOT contain the
        // far ends of the source text.
        Assert.DoesNotContain("Lorem", hit.Excerpts[0]);
        Assert.DoesNotContain("consectetur", hit.Excerpts[0]);
    }

    [Fact]
    public void Scan_treats_pattern_as_regex_when_compilable()
    {
        var text = "Code A1B2C3 then D4E5F6 done.";
        var hit = EpubScanner.Scan(text, [@"[A-Z][0-9][A-Z][0-9][A-Z][0-9]"], contextChars: 0);

        Assert.Single(hit.MatchedPatterns);
        Assert.Equal(2, hit.Excerpts.Count);
        Assert.Equal("A1B2C3", hit.Excerpts[0]);
        Assert.Equal("D4E5F6", hit.Excerpts[1]);
    }

    [Fact]
    public void Scan_falls_back_to_case_insensitive_substring_for_invalid_regex()
    {
        // An unbalanced character class throws on Regex compilation — the
        // scanner must fall back to literal, case-insensitive substring matching.
        var text = "x[unclosed y and X[UNCLOSED z";
        var hit = EpubScanner.Scan(text, ["[unclosed"], contextChars: 0);

        Assert.Single(hit.MatchedPatterns);
        Assert.Equal(2, hit.Excerpts.Count);
    }

    [Fact]
    public void Scan_merges_overlapping_context_windows_into_one_excerpt()
    {
        var text = "...AAA xxxx BBB...";
        var hit = EpubScanner.Scan(text, ["AAA", "BBB"], contextChars: 100);

        Assert.Equal(2, hit.MatchedPatterns.Count);
        Assert.Single(hit.Excerpts);
        Assert.Contains("AAA", hit.Excerpts[0]);
        Assert.Contains("BBB", hit.Excerpts[0]);
    }
}

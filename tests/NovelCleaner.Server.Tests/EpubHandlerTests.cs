using System.Text;
using NovelCleaner.Server.Services;

namespace NovelCleaner.Server.Tests;

public sealed class EpubHandlerTests
{
    [Fact]
    public void ApplyRemovals_removes_verbatim_substring()
    {
        var html = Encoding.UTF8.GetBytes("<p>Real prose. WATERMARK_ABC. More prose.</p>");
        var (out_, applied) = EpubHandler.ApplyRemovals(html, [new RemovalInstruction("WATERMARK_ABC", "uuid")]);

        Assert.Single(applied);
        Assert.Equal("WATERMARK_ABC", applied[0].Removed);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("WATERMARK_ABC", result);
        Assert.Contains("Real prose", result);
    }

    [Fact]
    public void ApplyRemovals_skips_instructions_that_do_not_match_verbatim()
    {
        var html = Encoding.UTF8.GetBytes("<p>Hello world.</p>");
        var (_, applied) = EpubHandler.ApplyRemovals(html, [new RemovalInstruction("NOT_HERE", "x")]);

        Assert.Empty(applied);
    }

    [Fact]
    public void ApplyRemovals_handles_multiple_instructions_in_order()
    {
        var html = Encoding.UTF8.GetBytes("<p>AAA something BBB else CCC.</p>");
        var (out_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction("AAA", "1"),
            new RemovalInstruction("BBB", "2"),
            new RemovalInstruction("CCC", "3"),
        ]);

        Assert.Equal(3, applied.Count);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("AAA", result);
        Assert.DoesNotContain("BBB", result);
        Assert.DoesNotContain("CCC", result);
    }

    [Fact]
    public void ApplyRemovals_strips_empty_tags_left_behind()
    {
        var html = Encoding.UTF8.GetBytes("<p>WATERMARK</p><p>Real text.</p>");
        var (out_, _) = EpubHandler.ApplyRemovals(html, [new RemovalInstruction("WATERMARK", "x")]);

        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("<p></p>", result);
        Assert.Contains("Real text.", result);
    }

    [Fact]
    public void ApplyRemovals_ignores_blank_or_whitespace_remove_strings()
    {
        var html = Encoding.UTF8.GetBytes("<p>Hello.</p>");
        var (_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction("",     "blank"),
            new RemovalInstruction("   ",  "whitespace"),
        ]);

        Assert.Empty(applied);
    }

    [Fact]
    public void ExtractText_strips_script_and_style_tags()
    {
        var html = Encoding.UTF8.GetBytes(
            "<html><head><style>body{color:red}</style></head><body><p>Hello world</p><script>alert(1)</script></body></html>");

        var text = EpubHandler.ExtractText(html);

        Assert.Equal("Hello world", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Fact]
    public void ExtractText_collapses_whitespace_runs()
    {
        var html = Encoding.UTF8.GetBytes("<p>line\n\n\twith    spaces</p>");

        var text = EpubHandler.ExtractText(html);

        Assert.Equal("line with spaces", text);
    }
}

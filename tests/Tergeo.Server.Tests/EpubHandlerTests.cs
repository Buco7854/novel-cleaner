using System.Text;
using Tergeo.Server.Services;

namespace Tergeo.Server.Tests;

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
    public void ApplyRemovals_matches_text_split_across_adjacent_tags()
    {
        // Real-world failure: AngleSharp's TextContent joins <a>X</a><a>Y</a>
        // into "XY" with no separator, so the LLM returns "XY" as one verbatim
        // string. The strict-substring matcher would miss that because the
        // raw HTML has "</a><a>" between the two halves.
        var html = Encoding.UTF8.GetBytes(
            "<p>Begin <a>Magdę Gessler</a><a>Otwarty</a> end</p>");

        var (out_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction("Magdę GesslerOtwarty", "ad")
        ]);

        Assert.Single(applied);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("Gessler", result);
        Assert.DoesNotContain("Otwarty", result);
        Assert.Contains("Begin", result);
        Assert.Contains("end", result);
    }

    [Fact]
    public void ApplyRemovals_matches_across_div_br_div_with_no_whitespace()
    {
        // Exact failure shape reported: a Polish ad headline split across
        // <div>…Gessler</div><br /><div>Otwarty</div>. AngleSharp's TextContent
        // concatenated to "…GesslerOtwarty", which is what the LLM returned
        // verbatim — but that string isn't a substring of the raw HTML, so the
        // strict IndexOf match used to miss it.
        var html = Encoding.UTF8.GetBytes(
            "<div>Polacy szturmują banki – wszystko przez Magdę Gessler</div>"
            + "<br /><div>Otwarty</div><br /><div>The story continues.</div>");

        var (out_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction(
                "Polacy szturmują banki – wszystko przez Magdę GesslerOtwarty",
                "polish ad")
        ]);

        Assert.Single(applied);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("Polacy", result);
        Assert.DoesNotContain("Gessler", result);
        Assert.DoesNotContain("Otwarty", result);
        Assert.Contains("The story continues.", result);
    }

    [Fact]
    public void ApplyRemovals_decodes_html_entities_in_match()
    {
        // The LLM gets the entity-decoded text from AngleSharp (& not &amp;),
        // so the matcher must decode entities in its projection too.
        var html = Encoding.UTF8.GetBytes(
            "<div>Sign up &amp; start meeting women in your area today.</div>");

        var (out_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction(
                "Sign up & start meeting women in your area today.",
                "ad")
        ]);

        Assert.Single(applied);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("Sign up", result);
        Assert.DoesNotContain("&amp;", result);
    }

    [Fact]
    public void ApplyRemovals_preserves_block_structure_when_match_spans_tags()
    {
        // Two ad divs joined by <br />. The LLM saw them concatenated as one
        // string ("...away!Sign up..."). When we delete the matched span we
        // must NOT vaporize the structural separator between *other* paragraphs
        // — keep tags, drop only visible chars.
        var html = Encoding.UTF8.GetBytes(
            "<div>Story before.</div><br />"
            + "<div>😍 Casual Date is just a click away!</div><br />"
            + "<div>Sign up &amp; start meeting women in your area today.</div><br />"
            + "<div>Story after.</div>");

        var (out_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction(
                "😍 Casual Date is just a click away!Sign up & start meeting women in your area today.",
                "ad block")
        ]);

        Assert.Single(applied);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("Casual Date", result);
        Assert.DoesNotContain("Sign up", result);
        // Bookend prose preserved.
        Assert.Contains("Story before.", result);
        Assert.Contains("Story after.", result);
        // The `<br />` between "Story before" and "Story after" must still be
        // present so the two surviving paragraphs aren't visually fused.
        Assert.Contains("<br />", result);
    }

    [Fact]
    public void ApplyRemovals_tolerates_whitespace_differences()
    {
        // LLM may collapse newlines/runs of spaces; matcher should still find
        // the corresponding span in the source.
        var html = Encoding.UTF8.GetBytes("<p>Lorem\n  ipsum   WATERMARK_X dolor</p>");

        var (out_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction("ipsum WATERMARK_X dolor", "test")
        ]);

        Assert.Single(applied);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("WATERMARK_X", result);
    }

    [Fact]
    public void ApplyRemovals_collapses_double_space_left_behind_by_token_removal()
    {
        // LLM removes ONLY the inserted token (per the prompt rule), without
        // absorbing the spaces around it. We must heal the resulting "  ".
        var html = Encoding.UTF8.GetBytes(
            "<p>The sheer /N_o_v_e_l_i_g_h_t/ size of the black tendrils.</p>");

        var (out_, applied) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction("/N_o_v_e_l_i_g_h_t/", "watermark token")
        ]);

        Assert.Single(applied);
        var result = Encoding.UTF8.GetString(out_);
        Assert.DoesNotContain("/N_o_v_e_l_i_g_h_t/", result);
        Assert.Contains("The sheer size of the black tendrils.", result);
        Assert.DoesNotContain("  ", result); // no double space
    }

    [Fact]
    public void ApplyRemovals_strips_orphan_space_before_punctuation()
    {
        // After "watermark." → "." we must drop the leading space too.
        var html = Encoding.UTF8.GetBytes("<p>End of preview WATERMARK_X.</p>");

        var (out_, _) = EpubHandler.ApplyRemovals(html, [
            new RemovalInstruction("WATERMARK_X", "test")
        ]);

        var result = Encoding.UTF8.GetString(out_);
        Assert.Contains("End of preview.", result);
        Assert.DoesNotContain("preview .", result);
        Assert.DoesNotContain("  ", result);
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

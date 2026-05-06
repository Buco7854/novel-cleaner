using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NovelCleaner.Server.Services;

public sealed record CleanResult(
    bool HasWatermarks,
    IReadOnlyList<RemovalInstruction> Items,
    string RawText);

public sealed class OpenAiClient(HttpClient http, ILogger<OpenAiClient> log)
{
    public const string DefaultSystemPrompt = """
        You are an expert at identifying watermarks and extraneous non-novel content in ebook text.

        Each request will tell you which mode it is using:
        - "Pattern mode" — you receive SHORT EXCERPTS extracted around heuristic pattern matches. The patterns may produce false positives, so judge each excerpt on its own merits.
        - "Full-page mode" — you receive WHOLE PAGES (chapters or sections) from the ebook with no pre-filtering. Most of the content will be legitimate prose; only flag the parts that are clearly not.

        THE CORE TEST
        For every candidate, ask: "Could the author or their official publisher have intentionally put this here?" If the answer is yes — even when the text isn't story prose — KEEP IT. Only flag content clearly inserted by a third party (a scraping site, a piracy aggregator, a watermarking distributor) without the author's involvement.

        KEEP (do not flag), even if it looks unusual or non-narrative:
        - Author's notes, prefaces, afterwords, "from the author" sections, status updates, hiatus announcements, Patreon / Ko-fi / Discord shout-outs written in the author's voice.
        - Promotion of the author's own work — "Book 2 is out now", "check out my other series", links to the author's official store / Amazon / Kobo / RoyalRoad / ScribbleHub page, requests to leave a review.
        - Tables of contents, chapter lists, "also by this author" pages, glossaries, character lists, dramatis personae, maps, content / trigger warnings, translator or editor notes on official translations.
        - Front / back matter from the publisher: copyright page, ISBN block, imprint, dedication, epigraph, acknowledgements, "About the author".
        - Chapter headings, scene breaks, in-character text that happens to look ad-like (e.g. an in-world poster or flyer quoted in the story).

        REMOVE (flag) only when the inserted text clearly does NOT come from the author or their publisher:
        1. Watermarks and tracking codes — UUIDs, distributor IDs, per-buyer "purchased by" lines, repeated brand tokens that split prose mid-sentence (a token jammed inside a sentence with no narrative function is the strongest signal).
        2. Third-party site advertisements — "Read the latest chapters on freenovel.example", "Visit XYZSite.com for updates", calls to register / log in to a reader site that the author would not endorse. The tell is a URL or site name pointing somewhere OTHER than the author's own channels, often dropped mid-paragraph with no transition.
        3. Aggregator / scraper boilerplate — header / footer text that's clearly templating from a piracy host, not authored content.

        TIE-BREAKER
        If you can't decide whether something is author-originated or third-party-injected, default to KEEP. Removing real prose or a real author note is a much worse failure than letting one ad through — the user can re-run later, but deleted text is gone.

        Return ONLY a JSON object with this structure:
        {
          "items": [
            {
              "remove": "<verbatim text to delete — copy it character-for-character from the input>",
              "reason": "<brief reason>"
            }
          ]
        }

        Rules:
        - Copy the text to remove VERBATIM — character-for-character from the input. No paraphrasing, summarising, or truncation.
        - Return the SMALLEST substring that captures the watermark / inserted text. Do NOT include any surrounding prose. If a watermark is embedded mid-sentence — for example "A familiar shape ⟦meta:tk-8821-X⟧ moved across the wall" — the item to remove is "⟦meta:tk-8821-X⟧" (just the inserted token), NEVER the whole sentence. Removing surrounding narrative text is unacceptable; it deletes the author's prose.
        - If a passage spans multiple lines, return each distinct part as a separate item.
        - Be conservative: when in doubt, do NOT include an item. Preserve all legitimate book content.
        - Return an empty items list if nothing in the input clearly qualifies.
        """;

    private const int MaxRetries = 4;
    private static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(5);

    public async Task<CleanResult> IdentifyWatermarksAsync(
        IReadOnlyList<string> excerpts,
        IReadOnlyList<string> matchedPatterns,
        string apiKey,
        string baseUrl,
        string model,
        CancellationToken ct,
        string? systemPrompt = null)
    {
        var isPatternMode = matchedPatterns.Count > 0;
        var modeHeader = isPatternMode
            ? $"Mode: Pattern mode.\nPatterns that flagged these excerpts (may have false positives):\n{string.Join("\n", matchedPatterns.Select(p => "  - " + p))}"
            : "Mode: Full-page mode. The text below is whole pages from the ebook with no pre-filtering — most of it is legitimate prose.";

        var bodyLabel = isPatternMode ? "Excerpt" : "Page";
        var bodyText = string.Join("\n\n---\n\n",
            excerpts.Select((e, i) => $"{bodyLabel} {i + 1}:\n{e}"));

        var prompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? DefaultSystemPrompt
            : DefaultSystemPrompt + "\n\nAdditional instructions:\n" + systemPrompt.Trim();
        var messages = new[]
        {
            new ChatMessage("system", prompt),
            new ChatMessage("user", $"{modeHeader}\n\n{bodyText}"),
        };

        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        var url = $"{root}/chat/completions";

        Exception? last = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var raw = await CallAsync(url, apiKey, model, messages, ct);
                return Parse(raw);
            }
            catch (RateLimitException ex)
            {
                last = ex;
                await Task.Delay(RetryBase * (1 << attempt), ct);
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                last = ex;
                log.LogWarning(ex, "LLM call failed (attempt {Attempt})", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            }
        }
        throw last ?? new InvalidOperationException("LLM call exhausted retries");
    }

    private async Task<string> CallAsync(string url, string apiKey, string model, ChatMessage[] messages, CancellationToken ct)
    {
        async Task<string> Send(object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RateLimitException();
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
            }
            var doc = await resp.Content.ReadFromJsonAsync<ChatCompletion>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Empty completion response");
            return doc.Choices?.FirstOrDefault()?.Message?.Content
                ?? throw new InvalidOperationException("Completion missing message content");
        }

        try
        {
            return await Send(new
            {
                model,
                messages,
                temperature = 0.1,
                response_format = new { type = "json_object" },
            });
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("400"))
        {
            // Some models reject response_format — fall back without it.
            return await Send(new { model, messages, temperature = 0.1 });
        }
    }

    private static CleanResult Parse(string raw)
    {
        var text = raw.Trim();
        var fence = Regex.Match(text, "```(?:json)?\\s*(\\{[\\s\\S]*?\\})\\s*```");
        if (fence.Success) text = fence.Groups[1].Value;
        var cleanedRaw = text;

        ParsedItems parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ParsedItems>(text, JsonOpts) ?? new();
        }
        catch
        {
            var brace = Regex.Match(text, "\\{[\\s\\S]*\\}");
            parsed = brace.Success
                ? JsonSerializer.Deserialize<ParsedItems>(brace.Value, JsonOpts) ?? new()
                : new();
        }

        var items = (parsed.Items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Remove))
            .Select(i => new RemovalInstruction(i.Remove!, i.Reason ?? ""))
            .ToArray();

        return new CleanResult(items.Length > 0, items, cleanedRaw);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class ChatCompletion
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }
    private sealed class Choice
    {
        [JsonPropertyName("message")] public Msg? Message { get; set; }
    }
    private sealed class Msg
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    private sealed class ParsedItems
    {
        [JsonPropertyName("items")] public List<ParsedItem>? Items { get; set; }
    }
    private sealed class ParsedItem
    {
        [JsonPropertyName("remove")] public string? Remove { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }
}

public sealed class RateLimitException : Exception
{
    public RateLimitException() : base("rate limited") { }
}

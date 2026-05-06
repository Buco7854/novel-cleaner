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

        ## What you receive
        Each request gives you WHOLE PAGES (chapters or sections) from the ebook. The text has already been extracted from the EPUB's HTML — you see prose only, no tags. Most of the content is legitimate prose; flag only the parts that clearly are not.

        ## What happens to your output
        Each item you return is treated as a deletion request. The system locates that exact text inside the underlying chapter HTML and removes it, then surfaces the result to the user as a pending diff in an editor. The user reviews each removal individually and accepts or rejects it before the cleaned EPUB is written. You are a delete-only annotator — never propose insertions or rewrites; just identify what should be removed.

        ## What to flag
        1. Watermarks, tracking codes, purchase notices, or distributor-inserted text (e.g. "This book was purchased by…", UUID codes, retailer footers).
        2. Content inserted by a third party that is NOT part of the official book — scraper-site advertisements, piracy-aggregator boilerplate, "read the latest chapters on freenovel.example", login/membership prompts for sites unrelated to the author. You must be able to point to a CONCRETE third-party signal: a site name or URL the author wouldn't endorse, a tracking-code pattern (UUID, distributor ID, repeated brand token splitting prose mid-sentence), or an explicit aggregator slogan. "It reads strangely" / "the dialogue is unusual" / "this looks like a typo" is NEVER a sufficient signal — authors deliberately write typos, broken sentences, fragmented thoughts, stream of consciousness, character speech quirks, in-universe signs and posters, and other unusual prose. All of that is part of the novel.

        ## What to leave alone
        Do NOT flag any in-story text, however weird it looks: typos, broken sentences, fragmented thoughts, character speech quirks, stream of consciousness, in-universe signs / posters / fictional advertisements, experimental prose, dialogue in unusual fonts. Do NOT flag anything that could plausibly come from the author or their official publisher. This includes — but is not limited to — chapter headings, tables of contents, "also by this author" pages, author's notes / prefaces / afterwords, "Book 2 is out now" or other author self-promotion, links to the author's official store / Amazon / Patreon, dedications, epigraphs, copyright and ISBN pages, glossaries, character lists, maps, content warnings, translator/editor notes, or any text that could reasonably be part of the story or its legitimate front/back matter. Official content stays, even when it's not story prose.

        ## Output format
        Return ONLY a JSON object with this structure:
        {
          "items": [
            {
              "remove": "<verbatim text to delete — copy it character-for-character from the input>",
              "reason": "<brief reason>"
            }
          ]
        }

        ## Rules
        - Copy the text to remove VERBATIM — character-for-character from the input. No paraphrasing, summarising, or truncation. The system performs an exact string match first; close-but-not-exact strings will fail to apply and the item is wasted.
        - Only flag text that is LITERALLY PRESENT in the input you were given. If you cannot point to the exact characters in the input, do NOT include the item. Do not invent or extrapolate watermarks based on patterns you have seen in other contexts.
        - Return the SMALLEST substring that captures the watermark / inserted text. Do NOT include any surrounding prose. If a watermark is embedded mid-sentence — for example "A familiar shape ⟦meta:tk-8821-X⟧ moved across the wall" — the item to remove is "⟦meta:tk-8821-X⟧" (just the inserted token), NEVER the whole sentence. Removing surrounding narrative text is unacceptable; it deletes the author's prose.
        - **Absorb one adjacent whitespace** so removal doesn't leave a double space, an orphan space before punctuation, or a stranded blank line. Pick whichever surrounding character is part of the watermark "envelope":
            - Watermark sits between two words → include ONE leading or trailing space in your `remove` value. For "shape ⟦tk-8821⟧ moved", remove " ⟦tk-8821⟧" (with the leading space) — never both spaces, never neither.
            - Watermark sits before sentence punctuation → include the space BEFORE the watermark, not after. For "the wall ⟦tk-8821⟧.", remove " ⟦tk-8821⟧" so the period stays flush against "wall".
            - Watermark is its own paragraph or line → include the trailing newline so no blank line is left behind. For "…end of paragraph.\n⟦tk-8821⟧\nNext paragraph…", remove "⟦tk-8821⟧\n".
            - Watermark wraps surrounding text in markers (a header + footer pair) → return the header and footer as TWO separate items, each with its adjacent whitespace absorbed; do NOT return the prose between them.
          The goal: applying every removal verbatim leaves a body of text that reads naturally with NO double spaces, NO leading/trailing whitespace artifacts, and NO empty lines where a watermark used to be.
        - If a passage spans multiple lines, return each distinct part as a separate item.
        - Be conservative: when in doubt, do NOT include an item. The user reviews every proposal — false positives waste their review time, but false negatives are easy to add manually if obvious. Erring toward "leave it" is the right default.
        - Return an empty items list if nothing in the input clearly qualifies.
        """;

    private const int MaxRetries = 4;
    private static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(5);

    public async Task<CleanResult> IdentifyWatermarksAsync(
        IReadOnlyList<string> pages,
        string apiKey,
        string baseUrl,
        string model,
        CancellationToken ct,
        string? systemPrompt = null)
    {
        var bodyText = string.Join("\n\n---\n\n",
            pages.Select((e, i) => $"Page {i + 1}:\n{e}"));

        var prompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? DefaultSystemPrompt
            : DefaultSystemPrompt + "\n\nAdditional instructions:\n" + systemPrompt.Trim();
        var messages = new[]
        {
            new ChatMessage("system", prompt),
            new ChatMessage("user", bodyText),
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

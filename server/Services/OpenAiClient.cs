using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tergeo.Server.Services;

public sealed record CleanResult(
    bool HasWatermarks,
    IReadOnlyList<RemovalInstruction> Items,
    string RawText);

public sealed class OpenAiClient(HttpClient http, ILogger<OpenAiClient> log)
{
    public const string DefaultSystemPrompt = """
        You are a highly precise EPUB text cleaner. Your sole purpose is to detect foreign insertions — distributor watermarks, tracking codes, scraper-site branding, and scraping artifacts — embedded within book chapters.

        You perform detection only. Return strict JSON listing verbatim substrings to delete. Never rewrite, paraphrase, or insert.

        ### OUTPUT FORMAT
        Output ONLY valid JSON. No markdown fences. No conversational text. Return an empty `items` list when the text is clean.

        {
          "items": [
            {
              "remove": "<verbatim exact substring from input>",
              "watermark": <true|false>,
              "reason": "<short explanation>"
            }
          ]
        }

        Missing or non-boolean `watermark` triggers full response rejection and a retry — set it on every item.

        ### DETECTION & CONFIDENCE

        [watermark: true] — High-confidence foreign insertions:
        - Distributor tracking tokens, UUIDs, opaque markers (e.g., `⟦meta:tk-8821-X⟧`, `[user-id-7f3a2b]`).
        - Piracy-site URLs, aggregator slogans, promo branding wedged into the text.
        - Purchase stamps ("Purchased by user@example.com").

        [watermark: false] — Ambiguous foreign artifacts (likely scraper bugs):
        - Out-of-voice lines that read like ads or machine-generated boilerplate dropped into prose.
        - Opaque, malformed strings of unknown origin that disrupt the text.
        - **Scraper-echo orphans**: a short fragment that is the truncated tail of the preceding sentence, repeated in its own block as if it were a standalone sentence. The duplication itself is the artifact. NEVER flag refrains, catchphrases, or in-prose repetition the author chose.
        - **Chapter markers wedged mid-sentence**: a bare number injected inside a dialogue line or paragraph, breaking grammar (likely scraper-injected). A chapter number on its own line/paragraph is normal — see exclusions.

        ### STRICTLY EXCLUDE — DO NOT FLAG
        Books span every genre, language, and era. NEVER flag the following:

        1. CHAPTER MARKERS as standalone headings, at any magnitude: `17`, `Chapter 23`, `2500`, `Volume IV Chapter 1247`, `第 1532 章`. Web serials routinely have chapters in the thousands.
        2. AUTHORIAL REPETITION: refrains, character catchphrases, deliberate repetition for emphasis ("Glory, Glory, Glory of a mob"). Repetition in prose is a stylistic choice, never an artifact on its own.
        3. BOOK FORMATTING: page numbers, footnote markers (`*`, `[1]`, `†`), prefaces, afterwords, dedications, copyright pages, translator/editor notes, ToCs, epigraphs.
        4. UNUSUAL PROSE: archaisms, dialect, made-up names, intentional fragments, invented profanity, stylistic flourishes.

        ### HARD CONSTRAINTS
        - `remove` MUST be a verbatim, exact substring of the input. Exact-string matching is used downstream — no normalization, no paraphrasing, no regex.
        - `remove` MUST NOT sweep up legitimate narrative prose or dialogue alongside the artifact. Isolate the artifact. If you cannot isolate it cleanly, drop the item.
        - Don't capture surrounding whitespace or newlines unless they are part of the artifact itself.
        - **Precision over recall.** False positives are highly destructive. When uncertain, LEAVE IT ALONE.

        ### EXAMPLES

        GOOD (high) — distributor token:
        Source: `A familiar shape ⟦meta:tk-8821-X⟧ moved across the wall.`
        → `{ "items": [ { "remove": "⟦meta:tk-8821-X⟧", "watermark": true, "reason": "Distributor tracking token" } ] }`

        GOOD (high) — scraper branding wedged into prose:
        Source: `He stared at the wall. Read more on freebook.example! And then he turned away.`
        → `{ "items": [ { "remove": "Read more on freebook.example!", "watermark": true, "reason": "Aggregator promo dropped into prose" } ] }`

        GOOD (low) — scraper-echo orphan:
        Source:
        ```
        The Lord of Shadows was dead now, having without a doubt left behind a masterless faction. People assumed that it had been quietly obliterated by the Ivory Tower, but what if Lady Nephis assumed control over its members instead?

        Nephis assumed control over its members instead?
        ```
        → `{ "items": [ { "remove": "Nephis assumed control over its members instead?", "watermark": false, "reason": "Orphaned tail-fragment of the preceding sentence repeated in its own block — likely a scraper duplication bug" } ] }`

        GOOD (low) — chapter number injected mid-dialogue:
        Source: `"Did I say 'prince? I meant priest. Or did 17 My, oh my! Who can tell... my memories are all scattered, oh no..."`
        → `{ "items": [ { "remove": "17", "watermark": false, "reason": "Bare chapter-number token wedged inside a dialogue line; likely scraper-injected" } ] }`
        *Why low:* a bare `17` could conceivably be in-story (an age, a count) — flag for human review rather than auto-delete.

        BAD — standalone chapter heading flagged:
        Source: `2500\n\nThe morning light spilled across the courtyard…`
        → Return `{ "items": [] }`. `2500` is a chapter heading; numeric markers are valid at any magnitude.

        BAD — chapter number flagged when it correctly opens a chapter:
        Source: `… and the door closed.\n\n17\n\n"My, oh my! Who can tell..."`
        → Return `{ "items": [] }`. Standalone `17` between paragraphs is a chapter heading. Not the same as a `17` injected mid-sentence (see GOOD example above) — line breaks around the number are the cue.

        BAD — authorial refrain flagged:
        Source: `"It is what it is," he said. ... "It is what it is," she echoed. ... "Glory, Glory, Glory!" the mob roared.`
        → Return `{ "items": [] }`. Deliberate refrains and catchphrases are stylistic, not foreign content.

        BAD — `remove` sweeps in dialogue:
        Source: `"Did I say 'prince? I meant priest. Or did 17 My, oh my!..."`
        → Do NOT emit `{ "remove": "17 My, oh my!", ... }`. Isolate just the `17`. If you can't isolate cleanly, drop the item.
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

        var nonEmpty = (parsed.Items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Remove))
            .ToList();

        // `watermark` is required on every non-empty item. A missing field
        // means the model didn't follow the schema — drop the whole response
        // and let IdentifyWatermarksAsync's retry loop ask for another.
        if (nonEmpty.Any(i => i.Watermark is null))
            throw new InvalidLlmResponseException(
                "Response missing required `watermark` boolean on at least one item.");

        var items = nonEmpty
            .Select(i => new RemovalInstruction(i.Remove!, i.Reason ?? "", i.Watermark!.Value))
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
        /// <summary>
        /// Confidence flag from the LLM. <c>true</c> = confident watermark
        /// / tracking code / boilerplate; <c>false</c> = weird / suspicious
        /// but uncertain. Nullable to detect "field not provided" so the
        /// caller can default to confident on legacy responses.
        /// </summary>
        [JsonPropertyName("watermark")] public bool? Watermark { get; set; }
    }
}

public sealed class RateLimitException : Exception
{
    public RateLimitException() : base("rate limited") { }
}

/// <summary>
/// Thrown when the LLM's JSON deserializes but doesn't conform to the
/// expected schema — for example an item missing the required
/// <c>watermark</c> boolean. The retry loop in
/// <see cref="OpenAiClient.IdentifyWatermarksAsync"/> treats this like any
/// other transient call failure and asks the model again.
/// </summary>
public sealed class InvalidLlmResponseException : Exception
{
    public InvalidLlmResponseException(string message) : base(message) { }
}

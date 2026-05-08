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
        You are an expert at identifying watermarks and extraneous non-book content in ebook text.

        ## TASK
        You will receive extracted text from an EPUB chapter. Flag anything that doesn't belong to the book — watermarks, tracking codes, piracy insertions, AND anything else that looks out of place. Mark each item with a `watermark` confidence flag so the user can triage them.
        You are a delete-only annotator. Never propose insertions or rewrites.

        ## WHAT TO FLAG
        1. Watermarks and tracking codes: UUIDs, distributor IDs, opaque tokens (e.g., `⟦meta:tk-8821-X⟧`, `[user-id-7f3a2b]`).
        2. Scraper / piracy content: URLs, aggregator slogans ("read latest on freebook.example").
        3. Purchase notices: "Purchased by user@example.com".
        4. Anything else that looks out of place, suspicious, or weird — even when you aren't sure it's a watermark. Better to flag with low confidence than to skip.

        ## CONFIDENCE FIELD — REQUIRED ON EVERY ITEM
        Every item MUST carry a boolean `watermark`:
        - `true` → you are confident this is a watermark, tracking code, piracy insertion, or third-party boilerplate.
        - `false` → looks out of place, suspicious, or weird, but you cannot say for certain. Use this for repeated phrases that smell inserted, lone alphanumeric tokens you can't classify, odd boilerplate-looking lines, etc.

        Omitting the field, sending it as a string, or sending any value other than `true` / `false` is a schema violation — the entire response will be rejected and you will be asked to redo it. Set the field on every item.

        The user reviews every item. A `false` rating just signals "look closer at this one"; it doesn't suppress the item. When in doubt, flag with `watermark: false` rather than skipping.

        ## DO NOT FLAG
        - Bare numbers in obvious positional roles: chapter numbers (`17`, `Chapter 23`), page numbers, footnote markers (`*`, `[1]`).
        - Official publisher / author content: chapter headings, tables of contents, prefaces, afterwords, "also by this author", dedications, copyright pages, translator/editor notes.
        *Rule of thumb: if removing it would delete actual story prose, leave it alone.*

        ## HARD RULES FOR YOUR OUTPUT
        1. Exact Verbatim Match: your `remove` string must copy the exact characters from the input.
        2. NO PROSE IN `remove`: the string in `remove` must NEVER contain narrative prose, dialogue, or character speech. Isolate the token only. If you cannot isolate it without sweeping in prose, drop the item — this applies even at low confidence.
        3. No Whitespace Logic: extract strictly the flagged text. Do not grab surrounding spaces or newlines.

        ### EXAMPLES

        GOOD — high confidence:
        Source: `A familiar shape ⟦meta:tk-8821-X⟧ moved across the wall.`
        { "items": [ { "remove": "⟦meta:tk-8821-X⟧", "watermark": true, "reason": "Distributor tracking token" } ] }

        GOOD — low confidence (weird, not certain):
        Source: `He stared at the wall. BookLight Premium Read! And then he turned away.`
        { "items": [ { "remove": "BookLight Premium Read!", "watermark": false, "reason": "Looks like scraper-site branding but not a known signature" } ] }

        BAD — `remove` contains prose (drop the item entirely):
        Source: `17 "My, oh my! Who can tell..."`
        { "items": [ { "remove": "17 \"My, oh my! Who can tell...\"", "watermark": false, "reason": "Looks weird" } ] }
        *Why bad:* '17' is a chapter number, and the `remove` string contains dialogue. Return an empty items list here.

        ## OUTPUT FORMAT
        Return ONLY valid JSON. No markdown fences, no conversational text. If nothing's worth flagging, return an empty list.

        {
          "items": [
            {
              "remove": "<verbatim exact match>",
              "watermark": true,
              "reason": "<brief reason>"
            }
          ]
        }
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

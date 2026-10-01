using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Codev;

public sealed record OllamaStructuredSummaryResult(string Summary, bool UsedStructuredOutput);

/// <summary>Requests bounded conversation summaries using Ollama JSON Schema output, with a validated text fallback.</summary>
public sealed class OllamaStructuredSummaryClient(HttpClient http, Uri endpoint)
{
    private static readonly JsonElement SummarySchema = JsonDocument.Parse("""
        {"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}
        """).RootElement.Clone();

    public async Task<OllamaStructuredSummaryResult> SummarizeAsync(string model,
        IReadOnlyList<ChatMessage> messages, int contextSize, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) throw new ArgumentException("Summary messages are required.", nameof(messages));

        var structured = await SendAsync(model, messages, contextSize, useSchema: true, cancellationToken).ConfigureAwait(false);
        if (structured is { } content && TryReadSummary(content, out var summary))
            return new OllamaStructuredSummaryResult(summary, UsedStructuredOutput: true);

        var fallbackMessages = messages.ToArray();
        fallbackMessages[0] = fallbackMessages[0] with
        {
            Content = fallbackMessages[0].Content + " For this fallback response, output only a concise plain-text summary, not JSON."
        };
        var plainText = await SendAsync(model, fallbackMessages, contextSize, useSchema: false, cancellationToken).ConfigureAwait(false);
        if (!TryReadPlainText(plainText, out summary))
            throw new InvalidOperationException("Ollama returned an empty or oversized summary in both structured and plain-text modes.");
        return new OllamaStructuredSummaryResult(summary, UsedStructuredOutput: false);
    }

    private async Task<string?> SendAsync(string model, IReadOnlyList<ChatMessage> messages, int contextSize,
        bool useSchema, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false,
            ["think"] = false,
            ["options"] = new Dictionary<string, object>
            {
                ["num_predict"] = 1500,
                ["num_ctx"] = contextSize > 0 ? contextSize : 32768
            }
        };
        if (useSchema) payload["format"] = SummarySchema;

        using var response = await http.PostAsJsonAsync(OllamaEndpoint.ApiUri(endpoint, "api/chat"), payload, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (useSchema && response.StatusCode == HttpStatusCode.BadRequest &&
                body.Contains("format", StringComparison.OrdinalIgnoreCase) &&
                (body.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("not support", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
                return null;
            throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
        }

        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.RootElement.TryGetProperty("message", out var message) &&
               message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }

    private static bool TryReadSummary(string? content, out string summary)
    {
        summary = "";
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var json = JsonDocument.Parse(content);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("summary", out var value) || value.ValueKind != JsonValueKind.String)
                return false;
            return TryReadPlainText(value.GetString(), out summary);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadPlainText(string? content, out string summary)
    {
        summary = content?.Trim() ?? "";
        return summary.Length is > 0 and <= ConversationCompactionService.MaxSummaryCharacters;
    }
}

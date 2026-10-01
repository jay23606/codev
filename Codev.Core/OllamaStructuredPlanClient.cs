using System.Net;
using System.Text;
using System.Text.Json;

namespace Codev;

public sealed record OllamaStructuredPlanResult(string Markdown, bool UsedStructuredOutput, int? PromptTokens);

/// <summary>Requests a read-only implementation plan with a JSON Schema response and a bounded text fallback.</summary>
public sealed class OllamaStructuredPlanClient(HttpClient http, Uri endpoint)
{
    public const int MaxPlanCharacters = 20_000;
    private const int MaxSteps = 16;
    private const int MaxListItems = 12;
    private static readonly JsonElement PlanSchema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "objective":{"type":"string"},
            "steps":{"type":"array","minItems":1,"maxItems":16,"items":{
              "type":"object",
              "properties":{
                "title":{"type":"string"},
                "details":{"type":"string"},
                "files":{"type":"array","maxItems":12,"items":{"type":"string"}}
              },
              "required":["title","details","files"],
              "additionalProperties":false
            }},
            "risks":{"type":"array","maxItems":12,"items":{"type":"string"}},
            "questions":{"type":"array","maxItems":12,"items":{"type":"string"}}
          },
          "required":["objective","steps","risks","questions"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public async Task<OllamaStructuredPlanResult> CreatePlanAsync(string model, IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, object>? options, bool think, Func<string, Task>? onRequestPayload = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) throw new ArgumentException("Plan messages are required.", nameof(messages));

        var structuredMessages = messages.ToArray();
        structuredMessages[0] = structuredMessages[0] with
        {
            Content = structuredMessages[0].Content + " Return only a JSON object matching the supplied schema. Include the objective, ordered actionable steps with likely file paths, risks, and unanswered questions. Do not use Markdown fences."
        };
        var structured = await SendAsync(model, structuredMessages, options, think, useSchema: true, onRequestPayload, cancellationToken)
            .ConfigureAwait(false);
        if (structured is { } response && TryReadPlan(response.Content, out var markdown))
            return new OllamaStructuredPlanResult(markdown, UsedStructuredOutput: true, response.PromptTokens);

        var fallbackMessages = messages.ToArray();
        fallbackMessages[0] = fallbackMessages[0] with
        {
            Content = fallbackMessages[0].Content + " For this fallback response, provide a concise ordered implementation plan as plain text or Markdown, not JSON."
        };
        var plainText = await SendAsync(model, fallbackMessages, options, think, useSchema: false, onRequestPayload, cancellationToken)
            .ConfigureAwait(false);
        if (!TryReadPlainText(plainText.Content, out markdown))
            throw new InvalidOperationException("Ollama returned an empty or oversized plan in both structured and plain-text modes.");
        return new OllamaStructuredPlanResult(markdown, UsedStructuredOutput: false, plainText.PromptTokens);
    }

    private sealed record OllamaPlanResponse(string? Content, int? PromptTokens);

    private async Task<OllamaPlanResponse> SendAsync(string model, IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, object>? options, bool think, bool useSchema,
        Func<string, Task>? onRequestPayload, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["keep_alive"] = OllamaRuntimeClient.ConversationKeepAlive,
            ["stream"] = false,
            ["think"] = think
        };
        if (options is { Count: > 0 }) payload["options"] = options;
        if (useSchema) payload["format"] = PlanSchema;

        var body = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        if (onRequestPayload is not null) await onRequestPayload(body).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, OllamaEndpoint.ApiUri(endpoint, "api/chat"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (useSchema && response.StatusCode == HttpStatusCode.BadRequest &&
                error.Contains("format", StringComparison.OrdinalIgnoreCase) &&
                (error.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("not support", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
                return new OllamaPlanResponse(null, null);
            throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {error}");
        }

        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = result.RootElement;
        var contentText = root.TryGetProperty("message", out var message) &&
                          message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
        var promptTokens = root.TryGetProperty("prompt_eval_count", out var tokenCount) && tokenCount.TryGetInt32(out var count)
            ? count
            : (int?)null;
        return new OllamaPlanResponse(contentText, promptTokens);
    }

    private static bool TryReadPlan(string? content, out string markdown)
    {
        markdown = "";
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
                !root.TryGetProperty("objective", out var objectiveElement) ||
                !TryReadText(objectiveElement, 2000, out var objective) ||
                !root.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array ||
                stepsElement.GetArrayLength() is < 1 or > MaxSteps ||
                !root.TryGetProperty("risks", out var risksElement) || !TryReadStringList(risksElement, out var risks) ||
                !root.TryGetProperty("questions", out var questionsElement) || !TryReadStringList(questionsElement, out var questions))
                return false;

            var steps = new List<(string Title, string Details, string[] Files)>();
            foreach (var step in stepsElement.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object || step.EnumerateObject().Count() != 3 ||
                    !step.TryGetProperty("title", out var titleElement) || !TryReadText(titleElement, 300, out var title) ||
                    !step.TryGetProperty("details", out var detailsElement) || !TryReadText(detailsElement, 2400, out var details) ||
                    !step.TryGetProperty("files", out var filesElement) || !TryReadStringList(filesElement, out var files, 240))
                    return false;
                steps.Add((title, details, files));
            }

            var output = new StringBuilder().AppendLine("## Objective").AppendLine().AppendLine(objective).AppendLine()
                .AppendLine("## Steps").AppendLine();
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                output.Append(index + 1).Append(". **").Append(step.Title).AppendLine("**");
                output.Append("   ").AppendLine(step.Details);
                if (step.Files.Length > 0)
                {
                    output.Append("   Files: ");
                    output.AppendLine(string.Join(", ", step.Files.Select(file => "`" + file.Replace("`", "'") + "`")));
                }
                output.AppendLine();
            }
            AppendList(output, "Risks", risks);
            AppendList(output, "Open questions", questions);
            markdown = output.ToString().TrimEnd();
            return markdown.Length <= MaxPlanCharacters;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void AppendList(StringBuilder output, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        output.Append("## ").AppendLine(title).AppendLine();
        foreach (var item in items) output.Append("- ").AppendLine(item);
        output.AppendLine();
    }

    private static bool TryReadStringList(JsonElement element, out string[] values, int maxCharacters = 1200)
    {
        values = [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxListItems) return false;
        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadText(item, maxCharacters, out var value)) return false;
            result.Add(value);
        }
        values = result.ToArray();
        return true;
    }

    private static bool TryReadText(JsonElement element, int maxCharacters, out string value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() ?? "" : "";
        return value.Length is > 0 && value.Length <= maxCharacters;
    }

    private static bool TryReadPlainText(string? content, out string plan)
    {
        plan = content?.Trim() ?? "";
        return plan.Length is > 0 and <= MaxPlanCharacters;
    }
}

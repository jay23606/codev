using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Codev;

public static class CloudModelProviders
{
    public const string OpenAI = "openai";
    public const string Anthropic = "anthropic";
    public static bool IsCloud(string? provider) => provider is OpenAI or Anthropic;
}

public sealed record CloudModel(string Provider, string Id, string DisplayName);
public sealed record CloudChatMessage(string Role, string Content);

/// <summary>Small REST client for hosted model discovery and text streaming. API keys are supplied per request and never persisted.</summary>
public sealed class CloudModelApiClient(HttpClient http)
{
    private static readonly Uri OpenAiBase = new("https://api.openai.com/v1/");
    private static readonly Uri AnthropicBase = new("https://api.anthropic.com/v1/");
    private const string AnthropicVersion = "2023-06-01";

    public async Task<IReadOnlyList<CloudModel>> ListModelsAsync(string provider, string apiKey, CancellationToken cancellationToken = default)
    {
        Validate(provider, apiKey);
        if (provider == CloudModelProviders.OpenAI)
        {
            var openAiModels = new List<CloudModel>();
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            do
            {
                var url = after is null
                    ? new Uri(OpenAiBase, "models")
                    : new UriBuilder(new Uri(OpenAiBase, "models")) { Query = $"after={Uri.EscapeDataString(after)}" }.Uri;
                using var request = CreateRequest(HttpMethod.Get, url, provider, apiKey);
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                        if (IsOpenAiTextModel(id)) openAiModels.Add(new CloudModel(provider, id!, id!));
                    }
                }
                var hasMore = root.TryGetProperty("has_more", out var hasMoreValue) && hasMoreValue.ValueKind == JsonValueKind.True;
                var next = root.TryGetProperty("last_id", out var lastId) && lastId.ValueKind == JsonValueKind.String ? lastId.GetString() : null;
                if (!hasMore || string.IsNullOrWhiteSpace(next) || !seenCursors.Add(next)) break;
                after = next;
            } while (true);
            return openAiModels.DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var models = new List<CloudModel>();
        var seenAnthropicCursors = new HashSet<string>(StringComparer.Ordinal);
        string? afterId = null;
        do
        {
            var url = new UriBuilder(new Uri(AnthropicBase, "models")) { Query = afterId is null ? "limit=1000" : $"limit=1000&after_id={Uri.EscapeDataString(afterId)}" }.Uri;
            using var request = CreateRequest(HttpMethod.Get, url, provider, apiKey);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idValue) || string.IsNullOrWhiteSpace(idValue.GetString())) continue;
                    var id = idValue.GetString()!;
                    var displayName = item.TryGetProperty("display_name", out var display) && !string.IsNullOrWhiteSpace(display.GetString()) ? display.GetString()! : id;
                    models.Add(new CloudModel(provider, id, displayName));
                }
            }
            var hasMore = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            var next = root.TryGetProperty("last_id", out var last) && last.ValueKind == JsonValueKind.String ? last.GetString() : null;
            afterId = hasMore && !string.IsNullOrWhiteSpace(next) && seenAnthropicCursors.Add(next) ? next : null;
        } while (afterId is not null);
        return models.DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string provider,
        string apiKey,
        string model,
        IReadOnlyList<CloudChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int? maxOutputTokens = null,
        Func<int, Task>? onInputTokenCount = null,
        Func<string, Task>? onRequestPayload = null)
    {
        Validate(provider, apiKey);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Choose a hosted model first.", nameof(model));
        if (maxOutputTokens is <= 0 or > 2048) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));

        var endpoint = provider == CloudModelProviders.OpenAI ? new Uri(OpenAiBase, "responses") : new Uri(AnthropicBase, "messages");
        var payload = provider == CloudModelProviders.OpenAI
            ? BuildOpenAiPayload(model, messages, maxOutputTokens)
            : BuildAnthropicPayload(model, messages, maxOutputTokens);
        var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var request = CreateRequest(HttpMethod.Post, endpoint, provider, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        if (onRequestPayload is not null) await onRequestPayload(payloadJson).ConfigureAwait(false);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var eventName = "";
        var eventData = new StringBuilder();
        var completed = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (eventData.Length == 0) { eventName = ""; continue; }
                var data = eventData.ToString();
                eventData.Clear();
                if (data == "[DONE]") { completed = true; eventName = ""; continue; }
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var type = GetString(root, "type") ?? eventName;
                if (type is "error" or "response.failed")
                    throw new InvalidOperationException(ReadApiError(root));
                if (onInputTokenCount is not null && TryReadInputTokenCount(provider, type, root) is { } inputTokenCount)
                    await onInputTokenCount(inputTokenCount).ConfigureAwait(false);
                if (provider == CloudModelProviders.OpenAI)
                {
                    if (type == "response.output_text.delta" && GetString(root, "delta") is { Length: > 0 } delta)
                        yield return delta;
                    else if (type == "response.incomplete")
                        throw new InvalidOperationException(ReadIncompleteResponse(root));
                    else if (type == "response.completed") completed = true;
                }
                else
                {
                    if (type == "content_block_delta" && root.TryGetProperty("delta", out var block) && GetString(block, "type") == "text_delta" && GetString(block, "text") is { Length: > 0 } text)
                        yield return text;
                    else if (type == "message_delta" && root.TryGetProperty("delta", out var messageDelta) && GetString(messageDelta, "stop_reason") == "max_tokens")
                        throw new InvalidOperationException("Anthropic stopped at the output token limit; the reply may be incomplete.");
                    else if (type == "message_stop") completed = true;
                }
                eventName = "";
                continue;
            }

            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].TrimStart();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart());
            }
        }
        if (!completed)
            throw new IOException($"The {ProviderDisplayName(provider)} stream ended before the provider reported a completed response.");
    }

    private static object BuildOpenAiPayload(string model, IReadOnlyList<CloudChatMessage> messages, int? maxOutputTokens)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = messages.Select(message => new { role = NormalizeRole(message.Role), content = message.Content }).ToArray(),
            ["stream"] = true,
            ["store"] = false
        };
        if (maxOutputTokens is { } max) payload["max_output_tokens"] = max;
        return payload;
    }

    private static object BuildAnthropicPayload(string model, IReadOnlyList<CloudChatMessage> messages, int? maxOutputTokens)
    {
        var system = string.Join("\n\n", messages.Where(message => message.Role is "system" or "developer").Select(message => message.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
        var chatMessages = messages.Where(message => message.Role is "user" or "assistant")
            .Select(message => new { role = message.Role, content = message.Content }).ToArray();
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxOutputTokens ?? 4096,
            ["messages"] = chatMessages,
            ["stream"] = true
        };
        if (!string.IsNullOrWhiteSpace(system)) payload["system"] = system;
        return payload;
    }

    private static string NormalizeRole(string role) => role == "developer" ? "system" : role;

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string provider, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        if (provider == CloudModelProviders.OpenAI)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string message;
        try
        {
            using var document = JsonDocument.Parse(details);
            message = ReadApiError(document.RootElement);
        }
        catch (JsonException) { message = details; }
        throw new HttpRequestException($"Hosted provider returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {message}", null, response.StatusCode);
    }

    private static string ReadApiError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object && GetString(error, "message") is { Length: > 0 } detail) return detail;
            if (error.ValueKind == JsonValueKind.String) return error.GetString()!;
        }
        if (root.TryGetProperty("response", out var response) && response.TryGetProperty("error", out var responseError) && GetString(responseError, "message") is { Length: > 0 } responseDetail)
            return responseDetail;
        return "The provider returned an error without details.";
    }

    private static string ReadIncompleteResponse(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) && response.TryGetProperty("incomplete_details", out var details) && GetString(details, "reason") is { Length: > 0 } reason)
            return $"The OpenAI response is incomplete ({reason}).";
        return "The OpenAI response is incomplete.";
    }

    private static int? TryReadInputTokenCount(string provider, string type, JsonElement root)
    {
        if (provider == CloudModelProviders.OpenAI && type == "response.completed" &&
            root.TryGetProperty("response", out var response) && response.TryGetProperty("usage", out var openAiUsage) &&
            openAiUsage.TryGetProperty("input_tokens", out var openAiInput) && openAiInput.TryGetInt32(out var openAiCount) && openAiCount >= 0)
            return openAiCount;
        if (provider == CloudModelProviders.Anthropic && type == "message_start" &&
            root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var anthropicUsage) &&
            anthropicUsage.TryGetProperty("input_tokens", out var anthropicInput) && anthropicInput.TryGetInt32(out var anthropicCount) && anthropicCount >= 0)
            return anthropicCount;
        return null;
    }

    private static string ProviderDisplayName(string provider) => provider == CloudModelProviders.OpenAI ? "OpenAI" : "Anthropic";

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsOpenAiTextModel(string? id) => id is not null &&
        (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
         id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) &&
        !ContainsAny(id, "audio", "transcribe", "realtime", "embedding", "moderation", "search", "tts", "image", "whisper");

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void Validate(string provider, string apiKey)
    {
        if (provider is not (CloudModelProviders.OpenAI or CloudModelProviders.Anthropic))
            throw new ArgumentOutOfRangeException(nameof(provider), "Choose OpenAI or Anthropic.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException($"Enter a {provider} API key before using hosted models.");
    }
}

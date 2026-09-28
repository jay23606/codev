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
            using var request = CreateRequest(HttpMethod.Get, new Uri(OpenAiBase, "models"), provider, apiKey);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            return data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(IsOpenAiTextModel)
                .Select(id => new CloudModel(provider, id!, id!))
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var models = new List<CloudModel>();
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
            afterId = root.TryGetProperty("has_more", out var more) && more.GetBoolean() && root.TryGetProperty("last_id", out var last) ? last.GetString() : null;
        } while (afterId is not null);
        return models;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string provider,
        string apiKey,
        string model,
        IReadOnlyList<CloudChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(provider, apiKey);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Choose a hosted model first.", nameof(model));

        var endpoint = provider == CloudModelProviders.OpenAI ? new Uri(OpenAiBase, "responses") : new Uri(AnthropicBase, "messages");
        var payload = provider == CloudModelProviders.OpenAI
            ? BuildOpenAiPayload(model, messages)
            : BuildAnthropicPayload(model, messages);
        using var request = CreateRequest(HttpMethod.Post, endpoint, provider, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (provider == CloudModelProviders.OpenAI)
            {
                var type = GetString(root, "type");
                if (type == "response.output_text.delta" && GetString(root, "delta") is { Length: > 0 } delta)
                    yield return delta;
                else if (type is "error" or "response.failed" || type == "response.incomplete")
                    throw new InvalidOperationException(ReadApiError(root));
            }
            else
            {
                var type = GetString(root, "type");
                if (type == "content_block_delta" && root.TryGetProperty("delta", out var block) && GetString(block, "type") == "text_delta" && GetString(block, "text") is { Length: > 0 } text)
                    yield return text;
                else if (type == "error")
                    throw new InvalidOperationException(ReadApiError(root));
            }
        }
    }

    private static object BuildOpenAiPayload(string model, IReadOnlyList<CloudChatMessage> messages) => new
    {
        model,
        input = messages.Select(message => new { role = NormalizeRole(message.Role), content = message.Content }).ToArray(),
        stream = true
    };

    private static object BuildAnthropicPayload(string model, IReadOnlyList<CloudChatMessage> messages)
    {
        var system = string.Join("\n\n", messages.Where(message => message.Role is "system" or "developer").Select(message => message.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
        var chatMessages = messages.Where(message => message.Role is "user" or "assistant")
            .Select(message => new { role = message.Role, content = message.Content }).ToArray();
        return new { model, max_tokens = 4096, system, messages = chatMessages, stream = true };
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

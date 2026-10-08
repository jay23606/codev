using System.Net.Http.Json;
using System.Text.Json;

namespace Codev;

public sealed record OllamaRunningModel(string Name, long Size, long SizeVram, int ContextLength, DateTimeOffset? ExpiresAt);

/// <summary>Reads and unloads models reported as resident by an Ollama server.</summary>
public sealed class OllamaRuntimeClient(HttpClient http, Uri endpoint)
{
    /// <summary>Keep models resident long enough for users to review a reply and send a follow-up without a cold reload.</summary>
    public const string ConversationKeepAlive = "30m";

    public async Task<IReadOnlyList<OllamaRunningModel>> ListRunningModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(OllamaEndpoint.ApiUri(endpoint, "api/ps"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];

        var result = new List<OllamaRunningModel>();
        foreach (var model in models.EnumerateArray())
        {
            var name = ReadString(model, "name") ?? ReadString(model, "model");
            if (string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new OllamaRunningModel(name, ReadInt64(model, "size"), ReadInt64(model, "size_vram"),
                ReadInt32(model, "context_length"), ReadDateTimeOffset(model, "expires_at")));
        }
        return result;
    }

    public async Task UnloadAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A loaded model name is required.", nameof(model));
        using var response = await http.PostAsJsonAsync(OllamaEndpoint.ApiUri(endpoint, "api/generate"),
            new { model, keep_alive = 0, stream = false }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static long ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : 0;

    private static int ReadInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.TryGetDateTimeOffset(out var value) ? value : null;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {body}", null, response.StatusCode);
    }
}

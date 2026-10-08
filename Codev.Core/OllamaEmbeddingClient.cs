using System.Net.Http.Json;
using System.Text.Json;

namespace Codev;

/// <summary>Thin client for Ollama's local /api/embed endpoint.</summary>
public sealed class OllamaEmbeddingClient(HttpClient http, Uri endpoint, string model)
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> input, CancellationToken cancellationToken = default)
    {
        if (input.Count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(input), "Embedding batches must contain 1–64 inputs.");
        using var response = await http.PostAsJsonAsync(OllamaEndpoint.ApiUri(endpoint, "api/embed"),
            new { model, input, truncate = false }, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama embedding request failed (HTTP {(int)response.StatusCode}). {body}", null, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("embeddings", out var vectors) || vectors.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Ollama returned no embeddings. Install an embedding model such as nomic-embed-text and check its name in Settings.");
        var result = vectors.EnumerateArray().Select(vector => vector.EnumerateArray().Select(value => value.GetSingle()).ToArray()).ToArray();
        if (result.Length != input.Count || result.Any(vector => vector.Length == 0) || result.Any(vector => vector.Length != result[0].Length))
            throw new InvalidOperationException("Ollama returned an invalid embedding vector count or dimensions.");
        return result;
    }
}

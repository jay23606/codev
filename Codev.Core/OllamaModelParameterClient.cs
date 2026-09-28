using System.Net.Http.Json;
using System.Text.Json;

namespace Codev;

/// <summary>Reads parameter values declared by a model using Ollama's model information endpoint.</summary>
public sealed class OllamaModelParameterClient(HttpClient http, Uri endpoint)
{
    public async Task<IReadOnlyDictionary<string, string>> GetDeclaredDefaultsAsync(string model,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model name is required.", nameof(model));
        using var response = await http.PostAsJsonAsync(OllamaEndpoint.ApiUri(endpoint, "api/show"),
            new { model, verbose = false }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {body}",
                null, response.StatusCode);
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("parameters", out var parameters)) return new Dictionary<string, string>();
        if (parameters.ValueKind == JsonValueKind.String) return ParseParameterText(parameters.GetString());
        if (parameters.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (property.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                values[property.Name] = property.Value.ToString();
        return values;
    }

    private static IReadOnlyDictionary<string, string> ParseParameterText(string? text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return parameters;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = line.StartsWith("PARAMETER ", StringComparison.OrdinalIgnoreCase) ? line[10..].TrimStart() : line;
            if (normalized.StartsWith('#')) continue;
            var separator = normalized.IndexOfAny([' ', '\t']);
            if (separator <= 0) continue;
            var name = normalized[..separator];
            var value = normalized[(separator + 1)..].Trim();
            if (value.Length == 0) continue;
            parameters[name] = value;
        }
        return parameters;
    }
}

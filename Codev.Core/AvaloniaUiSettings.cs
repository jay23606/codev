using System.Text.Json;

namespace Codev;

/// <summary>Local Avalonia UI preferences, including migration from the original theme-only JSON string.</summary>
public sealed record AvaloniaUiSettings(string Theme, string OllamaEndpoint, List<PromptTemplate>? PromptTemplates = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static AvaloniaUiSettings Default { get; } = new("dark", Codev.OllamaEndpoint.Default.ToString());

    public static AvaloniaUiSettings Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.String)
            return Default with { Theme = NormalizeTheme(document.RootElement.GetString()) };
        if (document.RootElement.ValueKind != JsonValueKind.Object) return Default;

        var value = document.RootElement.Deserialize<AvaloniaUiSettings>(JsonOptions) ?? Default;
        var endpoint = Codev.OllamaEndpoint.TryParse(value.OllamaEndpoint, out var parsed, out _)
            ? parsed.ToString()
            : Default.OllamaEndpoint;
        return new AvaloniaUiSettings(NormalizeTheme(value.Theme), endpoint,
            value.PromptTemplates is null ? null : PromptTemplateCatalog.Normalize(value.PromptTemplates));
    }

    public static string Serialize(AvaloniaUiSettings settings) => JsonSerializer.Serialize(settings);

    private static string NormalizeTheme(string? value) =>
        string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
}

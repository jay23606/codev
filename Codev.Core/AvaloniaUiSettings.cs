using System.Text.Json;

namespace Codev;

/// <summary>Local Avalonia UI preferences, including migration from the original theme-only JSON string.</summary>
public sealed record AvaloniaUiSettings(string Theme, string OllamaEndpoint, List<PromptTemplate>? PromptTemplates = null,
    List<SamplingPreset>? SamplingPresets = null, int ReadingWidth = 800, string? AutoConnectProvider = null,
    string FontFamily = "Inter", int FontSize = 14, bool PinnedConversationsExpanded = true,
    bool RecentConversationsExpanded = true, string EmbeddingModel = "nomic-embed-text")
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
            value.PromptTemplates is null ? null : PromptTemplateCatalog.Normalize(value.PromptTemplates),
            value.SamplingPresets is null ? null : SamplingPresetCatalog.Normalize(value.SamplingPresets).ToList(),
            NormalizeReadingWidth(value.ReadingWidth), NormalizeAutoConnectProvider(value.AutoConnectProvider),
            NormalizeFontFamily(value.FontFamily), NormalizeFontSize(value.FontSize), value.PinnedConversationsExpanded,
            value.RecentConversationsExpanded, NormalizeEmbeddingModel(value.EmbeddingModel));
    }

    public static string Serialize(AvaloniaUiSettings settings) => JsonSerializer.Serialize(settings);

    private static string NormalizeTheme(string? value) =>
        string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";

    public static int NormalizeReadingWidth(int value) => value switch
    {
        640 or 800 or 960 => value,
        0 => 0,
        _ => 800
    };

    public static string NormalizeFontFamily(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "inter" => "Inter",
        "segoe ui" => "Segoe UI",
        "arial" => "Arial",
        "consolas" => "Consolas",
        "aptos" => "Aptos",
        "calibri" => "Calibri",
        "verdana" => "Verdana",
        "tahoma" => "Tahoma",
        "georgia" => "Georgia",
        "cascadia code" => "Cascadia Code",
        _ => "Inter"
    };

    public static int NormalizeFontSize(int value) => value is 10 or 11 or 12 or 13 or 14 or 15 or 16 or 18 or 20 or 22 or 24 or 28 or 32 ? value : 14;

    public static string NormalizeEmbeddingModel(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl)
        ? "nomic-embed-text" : value.Trim();

    private static string? NormalizeAutoConnectProvider(string? value) => value?.ToLowerInvariant() switch
    {
        CloudModelProviders.OpenAI => CloudModelProviders.OpenAI,
        CloudModelProviders.Anthropic => CloudModelProviders.Anthropic,
        _ => null
    };
}

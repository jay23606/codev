namespace Codev;

/// <summary>Validates optional Responses API controls that are available on GPT-5 and o-series models.</summary>
public static class OpenAiGenerationSettings
{
    public static bool SupportsReasoningControls(string? model)
    {
        if (model is null) return false;
        if (model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) return true;
        if (!model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return false;
        var majorVersion = model[4..].Split(['.', '-'], 2)[0];
        return int.TryParse(majorVersion, out var major) && major >= 5;
    }

    public static string? NormalizeEffort(string? value) => Normalize(value, "low", "medium", "high");
    public static string? NormalizeVerbosity(string? value) => Normalize(value, "low", "medium", "high");

    private static string? Normalize(string? value, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }

    public static void AddToPayload(IDictionary<string, object> payload, string? model, string? effort, string? verbosity)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!SupportsReasoningControls(model)) return;
        if (NormalizeEffort(effort) is { } normalizedEffort)
            payload["reasoning"] = new Dictionary<string, object> { ["effort"] = normalizedEffort };
        if (NormalizeVerbosity(verbosity) is { } normalizedVerbosity)
            payload["text"] = new Dictionary<string, object> { ["verbosity"] = normalizedVerbosity };
    }
}

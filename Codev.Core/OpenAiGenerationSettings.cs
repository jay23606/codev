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

    public static IReadOnlyList<string> ReasoningEffortOptions(string? model)
    {
        if (!SupportsReasoningControls(model)) return [];
        if (model!.StartsWith("gpt-5-pro", StringComparison.OrdinalIgnoreCase)) return ["high"];
        var options = new List<string> { "low", "medium", "high" };
        if (IsAtLeastGpt51(model)) options.Insert(0, "none");
        if (SupportsExtraHighEffort(model)) options.Add("xhigh");
        return options;
    }

    public static string? NormalizeEffort(string? value, string? model = null)
    {
        var normalized = Normalize(value, "none", "low", "medium", "high", "xhigh");
        if (normalized is null || model is null) return normalized;
        return ReasoningEffortOptions(model).Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }

    public static string? NormalizeVerbosity(string? value) => Normalize(value, "low", "medium", "high");

    private static bool IsAtLeastGpt51(string model) => TryGetGptVersion(model, out var major, out var minor) &&
        (major > 5 || major == 5 && minor >= 1);

    private static bool SupportsExtraHighEffort(string model) =>
        model.StartsWith("gpt-5.1-codex-max", StringComparison.OrdinalIgnoreCase) ||
        TryGetGptVersion(model, out var major, out var minor) && (major > 5 || major == 5 && minor > 1);

    private static bool TryGetGptVersion(string model, out int major, out int minor)
    {
        major = minor = 0;
        if (!model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return false;
        var version = model[4..].Split('-', 2)[0].Split('.', 2);
        return int.TryParse(version[0], out major) && (version.Length == 1 || int.TryParse(version[1], out minor));
    }

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
        if (NormalizeEffort(effort, model) is { } normalizedEffort)
            payload["reasoning"] = new Dictionary<string, object> { ["effort"] = normalizedEffort };
        if (NormalizeVerbosity(verbosity) is { } normalizedVerbosity)
            payload["text"] = new Dictionary<string, object> { ["verbosity"] = normalizedVerbosity };
    }
}

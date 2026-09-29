namespace Codev;

/// <summary>Validates optional Responses API controls that are available on OpenAI reasoning models.</summary>
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
        if (SupportsMinimalEffort(model)) options.Insert(0, "minimal");
        if (SupportsNoneEffort(model)) options.Insert(0, "none");
        if (SupportsExtraHighEffort(model)) options.Add("xhigh");
        if (SupportsMaxEffort(model)) options.Add("max");
        return options;
    }

    public static IReadOnlyList<string> ReasoningModeOptions(string? model) =>
        SupportsProMode(model) ? ["pro"] : [];

    public static string? NormalizeReasoningMode(string? value, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "standard") return null;
        if (normalized != "pro" || model is null || !SupportsProMode(model)) return null;
        return normalized;
    }

    public static bool SupportsProMode(string? model) =>
        model is not null && !model.StartsWith("gpt-6-astra", StringComparison.OrdinalIgnoreCase) &&
        (TryGetGptVersion(model, out var major, out var minor) &&
            (major > 5 || major == 5 && minor >= 6) ||
         model.StartsWith("gpt-5.2-pro", StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeEffort(string? value, string? model = null)
    {
        var normalized = Normalize(value, "none", "minimal", "low", "medium", "high", "xhigh", "max");
        if (normalized is null || model is null) return normalized;
        return ReasoningEffortOptions(model).Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }

    public static string? NormalizeVerbosity(string? value) => Normalize(value, "low", "medium", "high");

    private static bool IsAtLeastGpt51(string model) => TryGetGptVersion(model, out var major, out var minor) &&
        (major > 5 || major == 5 && minor >= 1);

    private static bool SupportsMinimalEffort(string model) =>
        TryGetGptVersion(model, out var major, out _) && major == 5 && !IsAtLeastGpt51(model);

    private static bool SupportsNoneEffort(string model) =>
        IsAtLeastGpt51(model) && !model.StartsWith("gpt-6-astra", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsExtraHighEffort(string model) =>
        model.StartsWith("gpt-5.1-codex-max", StringComparison.OrdinalIgnoreCase) ||
        TryGetGptVersion(model, out var major, out var minor) && (major > 5 || major == 5 && minor > 1);

    private static bool SupportsMaxEffort(string model) =>
        TryGetGptVersion(model, out var major, out var minor) && (major > 5 || major == 5 && minor >= 6);

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

    public static void AddToPayload(IDictionary<string, object> payload, string? model, string? effort, string? verbosity,
        string? reasoningMode = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!SupportsReasoningControls(model)) return;
        var allowedEfforts = ReasoningEffortOptions(model);
        var allowedModes = ReasoningModeOptions(model);
        var normalizedMode = allowedModes.Contains(reasoningMode, StringComparer.Ordinal) && allowedEfforts.Count > 0
            ? reasoningMode : null;
        var normalizedEffort = allowedEfforts.Contains(Normalize(effort, "none", "minimal", "low", "medium", "high", "xhigh", "max"), StringComparer.Ordinal)
            ? Normalize(effort, "none", "minimal", "low", "medium", "high", "xhigh", "max") : null;
        if (normalizedEffort is not null || normalizedMode is not null)
        {
            var reasoning = new Dictionary<string, object>();
            if (normalizedEffort is not null) reasoning["effort"] = normalizedEffort;
            if (normalizedMode is not null) reasoning["mode"] = normalizedMode;
            payload["reasoning"] = reasoning;
        }
        if (NormalizeVerbosity(verbosity) is { } normalizedVerbosity)
            payload["text"] = new Dictionary<string, object> { ["verbosity"] = normalizedVerbosity };
    }
}

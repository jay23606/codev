using System.Text.Json;

namespace Codev;

public sealed record SamplingPreset(
    string Name,
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    double? PresencePenalty = null,
    double? RepeatPenalty = null,
    int? NumPredict = null);

/// <summary>Validates user-authored, portable Ollama sampling presets.</summary>
public static class SamplingPresetCatalog
{
    public const int MaxPresets = 30;
    public const int MaxNameLength = 48;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static IReadOnlyList<SamplingPreset> Normalize(IEnumerable<SamplingPreset?>? presets)
    {
        var result = new List<SamplingPreset>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets ?? [])
        {
            if (preset is null || !TryNormalize(preset, out var normalized) || !names.Add(normalized.Name)) continue;
            result.Add(normalized);
            if (result.Count == MaxPresets) break;
        }
        return result;
    }

    public static bool TryNormalize(SamplingPreset preset, out SamplingPreset normalized)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var name = preset.Name?.Trim() ?? "";
        var valid = name.Length is > 0 and <= MaxNameLength &&
            (preset.Temperature is null || Codev.ConversationSamplingSettings.NormalizeTemperature(preset.Temperature) is not null) &&
            (preset.TopP is null || Codev.ConversationSamplingSettings.NormalizeProbability(preset.TopP) is not null) &&
            (preset.TopK is null || Codev.ConversationSamplingSettings.NormalizeTopK(preset.TopK) is not null) &&
            (preset.PresencePenalty is null || Codev.ConversationSamplingSettings.NormalizePenalty(preset.PresencePenalty) is not null) &&
            (preset.RepeatPenalty is null || Codev.ConversationSamplingSettings.NormalizePenalty(preset.RepeatPenalty) is not null) &&
            (preset.NumPredict is null || Codev.ConversationSamplingSettings.NormalizeOutputTokens(preset.NumPredict) is not null) &&
            (preset.Temperature.HasValue || preset.TopP.HasValue || preset.TopK.HasValue || preset.PresencePenalty.HasValue || preset.RepeatPenalty.HasValue || preset.NumPredict.HasValue);
        normalized = valid
            ? preset with { Name = name, Temperature = Round(preset.Temperature), TopP = Round(preset.TopP), PresencePenalty = Round(preset.PresencePenalty), RepeatPenalty = Round(preset.RepeatPenalty) }
            : preset;
        return valid;
    }

    public static SamplingPreset Deserialize(string json)
    {
        var preset = JsonSerializer.Deserialize<SamplingPreset>(json, JsonOptions)
            ?? throw new InvalidDataException("The selected file does not contain a sampling preset.");
        if (!TryNormalize(preset, out var normalized))
            throw new InvalidDataException("The preset name or one of its sampling values is invalid.");
        return normalized;
    }

    public static string Serialize(SamplingPreset preset)
    {
        if (!TryNormalize(preset, out var normalized)) throw new ArgumentException("The sampling preset is invalid.", nameof(preset));
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static double? Round(double? value) => value is double number ? Math.Round(number, 2) : null;
}

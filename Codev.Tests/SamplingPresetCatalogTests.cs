using Codev;

namespace Codev.Tests;

public sealed class SamplingPresetCatalogTests
{
    [Fact]
    public void Normalizes_names_and_round_trips_a_portable_preset()
    {
        var preset = new SamplingPreset("  Focused code  ", Temperature: 0.3333, TopP: 0.9, NumPredict: 2048);

        var normalized = SamplingPresetCatalog.Deserialize(SamplingPresetCatalog.Serialize(preset));

        Assert.Equal("Focused code", normalized.Name);
        Assert.Equal(0.33, normalized.Temperature);
        Assert.Equal(0.9, normalized.TopP);
        Assert.Equal(2048, normalized.NumPredict);
    }

    [Fact]
    public void Rejects_invalid_values_and_empty_presets()
    {
        Assert.Throws<InvalidDataException>(() => SamplingPresetCatalog.Deserialize("""{"Name":"Bad","Temperature":9}"""));
        Assert.Throws<InvalidDataException>(() => SamplingPresetCatalog.Deserialize("""{"Name":"Empty"}"""));
    }

    [Fact]
    public void Normalization_deduplicates_names_and_limits_the_catalog()
    {
        var presets = Enumerable.Range(0, SamplingPresetCatalog.MaxPresets + 2)
            .Select(index => new SamplingPreset($"Preset {index}", Temperature: 0.5))
            .Prepend(new SamplingPreset("preset 0", Temperature: 0.2));

        var normalized = SamplingPresetCatalog.Normalize(presets);

        Assert.Equal(SamplingPresetCatalog.MaxPresets, normalized.Count);
        Assert.Equal(1, normalized.Count(preset => preset.Name.Equals("Preset 0", StringComparison.OrdinalIgnoreCase)));
    }
}

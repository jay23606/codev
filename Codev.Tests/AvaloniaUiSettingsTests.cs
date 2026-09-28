using Codev;

namespace Codev.Tests;

public sealed class AvaloniaUiSettingsTests
{
    [Theory]
    [InlineData("\"dark\"", "dark")]
    [InlineData("\"light\"", "light")]
    public void Migrates_legacy_theme_only_settings(string json, string expectedTheme)
    {
        var settings = AvaloniaUiSettings.Deserialize(json);

        Assert.Equal(expectedTheme, settings.Theme);
        Assert.Equal(OllamaEndpoint.Default.ToString(), settings.OllamaEndpoint);
    }

    [Fact]
    public void Restores_endpoint_and_theme_from_current_settings()
    {
        var settings = AvaloniaUiSettings.Deserialize("""{"Theme":"light","OllamaEndpoint":"https://ollama.example/base"}""");

        Assert.Equal("light", settings.Theme);
        Assert.Equal("https://ollama.example/base/", settings.OllamaEndpoint);
        Assert.Equal(800, settings.ReadingWidth);
    }

    [Theory]
    [InlineData(640, 640)]
    [InlineData(800, 800)]
    [InlineData(960, 960)]
    [InlineData(0, 0)]
    [InlineData(900, 800)]
    [InlineData(-1, 800)]
    public void Reading_width_accepts_supported_values_and_defaults_invalid_values(int input, int expected)
    {
        var settings = AvaloniaUiSettings.Deserialize($"{{\"ReadingWidth\":{input}}}");

        Assert.Equal(expected, settings.ReadingWidth);
    }

    [Fact]
    public void Round_trips_saved_prompt_templates()
    {
        var original = new AvaloniaUiSettings("dark", OllamaEndpoint.Default.ToString(),
            [new PromptTemplate("Review", "Review this project carefully.")],
            [new SamplingPreset("Precise", Temperature: 0.25, TopP: 0.9, NumPredict: 2048)], 960);

        var restored = AvaloniaUiSettings.Deserialize(AvaloniaUiSettings.Serialize(original));

        Assert.Equal(original.PromptTemplates, restored.PromptTemplates);
        Assert.Equal(original.SamplingPresets, restored.SamplingPresets);
        Assert.Equal(original.ReadingWidth, restored.ReadingWidth);
    }

    [Fact]
    public void Invalid_or_credential_bearing_endpoint_falls_back_to_loopback()
    {
        var settings = AvaloniaUiSettings.Deserialize("""{"Theme":"unexpected","OllamaEndpoint":"https://user:secret@ollama.example"}""");

        Assert.Equal("dark", settings.Theme);
        Assert.Equal(OllamaEndpoint.Default.ToString(), settings.OllamaEndpoint);
        Assert.DoesNotContain("secret", AvaloniaUiSettings.Serialize(settings));
    }
}

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

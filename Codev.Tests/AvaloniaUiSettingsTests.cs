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
        Assert.True(settings.PinnedConversationsExpanded);
        Assert.True(settings.RecentConversationsExpanded);
        Assert.Equal("nomic-embed-text", settings.EmbeddingModel);
        Assert.Equal(ProjectCommandPermissionMode.Auto, settings.DefaultProjectCommandPermissionMode);
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
            [new SamplingPreset("Precise", Temperature: 0.25, TopP: 0.9, NumPredict: 2048)], 960,
            AutoConnectProvider: "openai", FontFamily: "Segoe UI", FontSize: 18,
            PinnedConversationsExpanded: false, RecentConversationsExpanded: true, EmbeddingModel: "custom-embed-model");

        var restored = AvaloniaUiSettings.Deserialize(AvaloniaUiSettings.Serialize(original));

        Assert.Equal(original.PromptTemplates, restored.PromptTemplates);
        Assert.Equal(original.SamplingPresets, restored.SamplingPresets);
        Assert.Equal(original.ReadingWidth, restored.ReadingWidth);
        Assert.Equal(original.AutoConnectProvider, restored.AutoConnectProvider);
        Assert.Equal(original.FontFamily, restored.FontFamily);
        Assert.Equal(original.FontSize, restored.FontSize);
        Assert.Equal(original.PinnedConversationsExpanded, restored.PinnedConversationsExpanded);
        Assert.Equal(original.RecentConversationsExpanded, restored.RecentConversationsExpanded);
        Assert.Equal("custom-embed-model", restored.EmbeddingModel);
        Assert.Equal(original.DefaultProjectCommandPermissionMode, restored.DefaultProjectCommandPermissionMode);
    }

    [Theory]
    [InlineData(ProjectCommandPermissionMode.Auto)]
    [InlineData(ProjectCommandPermissionMode.AskEveryTime)]
    [InlineData(ProjectCommandPermissionMode.Allowlist)]
    [InlineData(ProjectCommandPermissionMode.ReadOnly)]
    public void Default_command_mode_round_trips(ProjectCommandPermissionMode mode)
    {
        var settings = new AvaloniaUiSettings("dark", OllamaEndpoint.Default.ToString(), DefaultProjectCommandPermissionMode: mode);

        Assert.Equal(mode, AvaloniaUiSettings.Deserialize(AvaloniaUiSettings.Serialize(settings)).DefaultProjectCommandPermissionMode);
    }

    [Fact]
    public void Invalid_default_command_mode_falls_back_to_auto()
    {
        var settings = AvaloniaUiSettings.Deserialize("{\"DefaultProjectCommandPermissionMode\":999}");

        Assert.Equal(ProjectCommandPermissionMode.Auto, settings.DefaultProjectCommandPermissionMode);
    }

    [Theory]
    [InlineData(0, ProjectCommandPermissionMode.AskEveryTime)]
    [InlineData(1, ProjectCommandPermissionMode.Auto)]
    [InlineData(2, ProjectCommandPermissionMode.Allowlist)]
    [InlineData(3, ProjectCommandPermissionMode.ReadOnly)]
    public void Migrates_numeric_default_command_modes_and_serializes_them_by_name(int storedMode,
        ProjectCommandPermissionMode expectedMode)
    {
        var settings = AvaloniaUiSettings.Deserialize($"{{\"DefaultProjectCommandPermissionMode\":{storedMode}}}");

        Assert.Equal(expectedMode, settings.DefaultProjectCommandPermissionMode);
        Assert.Contains("\"DefaultProjectCommandPermissionMode\":\"" + expectedMode + "\"",
            AvaloniaUiSettings.Serialize(settings));
    }

    [Theory]
    [InlineData("Consolas", "Consolas")]
    [InlineData("aptos", "Aptos")]
    [InlineData("CALIBRI", "Calibri")]
    [InlineData("Cascadia Code", "Cascadia Code")]
    [InlineData("Georgia", "Georgia")]
    [InlineData("segoe ui", "Segoe UI")]
    [InlineData("Comic Sans", "Inter")]
    public void Normalizes_font_family(string input, string expected)
    {
        var settings = AvaloniaUiSettings.Deserialize(System.Text.Json.JsonSerializer.Serialize(new { FontFamily = input }));

        Assert.Equal(expected, settings.FontFamily);
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(10, 10)]
    [InlineData(11, 11)]
    [InlineData(13, 13)]
    [InlineData(15, 15)]
    [InlineData(18, 18)]
    [InlineData(22, 22)]
    [InlineData(24, 24)]
    [InlineData(28, 28)]
    [InlineData(32, 32)]
    [InlineData(17, 14)]
    [InlineData(40, 14)]
    public void Normalizes_font_size(int input, int expected)
    {
        var settings = AvaloniaUiSettings.Deserialize(System.Text.Json.JsonSerializer.Serialize(new { FontSize = input }));

        Assert.Equal(expected, settings.FontSize);
    }

    [Theory]
    [InlineData("openai", "openai")]
    [InlineData("ANTHROPIC", "anthropic")]
    [InlineData("unknown", null)]
    public void Normalizes_auto_connect_provider(string input, string? expected)
    {
        var settings = AvaloniaUiSettings.Deserialize($"{{\"AutoConnectProvider\":\"{input}\"}}");

        Assert.Equal(expected, settings.AutoConnectProvider);
    }

    [Fact]
    public void Serializes_auto_connect_provider_without_credentials()
    {
        var settings = new AvaloniaUiSettings("dark", OllamaEndpoint.Default.ToString(), AutoConnectProvider: "openai");

        var json = AvaloniaUiSettings.Serialize(settings);

        Assert.Equal("openai", AvaloniaUiSettings.Deserialize(json).AutoConnectProvider);
        Assert.DoesNotContain("API_KEY", json);
    }

    [Fact]
    public void Round_trips_explicit_hosted_auto_connect_opt_out()
    {
        var settings = new AvaloniaUiSettings("dark", OllamaEndpoint.Default.ToString(),
            AutoConnectProviderPreferenceSet: true);

        var restored = AvaloniaUiSettings.Deserialize(AvaloniaUiSettings.Serialize(settings));

        Assert.Null(restored.AutoConnectProvider);
        Assert.True(restored.AutoConnectProviderPreferenceSet);
    }

    [Fact]
    public void Explicit_hosted_auto_connect_opt_out_wins_over_saved_keys()
    {
        var provider = AvaloniaUiSettings.ResolveAutoConnectProvider(null, preferenceSet: true,
            [CloudModelProviders.OpenAI, CloudModelProviders.Anthropic], CloudModelProviders.OpenAI);

        Assert.Null(provider);
    }

    [Fact]
    public void Unconfigured_hosted_auto_connect_prefers_active_provider_then_saved_provider_order()
    {
        var savedProviders = new[] { CloudModelProviders.OpenAI, CloudModelProviders.Anthropic };

        Assert.Equal(CloudModelProviders.Anthropic,
            AvaloniaUiSettings.ResolveAutoConnectProvider(null, preferenceSet: false, savedProviders,
                CloudModelProviders.Anthropic));
        Assert.Equal(CloudModelProviders.OpenAI,
            AvaloniaUiSettings.ResolveAutoConnectProvider(null, preferenceSet: false, savedProviders, "ollama"));
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

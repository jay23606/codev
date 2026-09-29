using System.Text.Json;

namespace Codev.Tests;

public sealed class OpenAiGenerationSettingsTests
{
    [Theory]
    [InlineData("gpt-5.4", true)]
    [InlineData("gpt-6-codex", true)]
    [InlineData("gpt-4.1", false)]
    [InlineData("o3", true)]
    [InlineData("o4-mini", true)]
    [InlineData("claude-sonnet", false)]
    public void Detects_models_with_reasoning_controls(string model, bool expected) =>
        Assert.Equal(expected, OpenAiGenerationSettings.SupportsReasoningControls(model));

    [Fact]
    public void Adds_only_selected_controls_for_supported_models()
    {
        var payload = new Dictionary<string, object>();

        OpenAiGenerationSettings.AddToPayload(payload, "gpt-5.4", "HIGH", "low");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal("high", document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("low", document.RootElement.GetProperty("text").GetProperty("verbosity").GetString());
    }

    [Fact]
    public void Omits_controls_for_unsupported_models_and_invalid_values()
    {
        var unsupported = new Dictionary<string, object>();
        OpenAiGenerationSettings.AddToPayload(unsupported, "gpt-4.1", "high", "low");
        Assert.Empty(unsupported);

        var invalid = new Dictionary<string, object>();
        OpenAiGenerationSettings.AddToPayload(invalid, "gpt-5", "xhigh", "verbose");
        Assert.Empty(invalid);
    }

    [Fact]
    public void Offers_only_documented_effort_values_for_restricted_models()
    {
        Assert.Equal(["high"], OpenAiGenerationSettings.ReasoningEffortOptions("gpt-5-pro"));
        Assert.Null(OpenAiGenerationSettings.NormalizeEffort("low", "gpt-5-pro"));
        var proPayload = new Dictionary<string, object>();
        OpenAiGenerationSettings.AddToPayload(proPayload, "gpt-5-pro", "low", "low");
        Assert.DoesNotContain("reasoning", proPayload.Keys);
        Assert.Contains("text", proPayload.Keys);
        Assert.Equal(["none", "low", "medium", "high"], OpenAiGenerationSettings.ReasoningEffortOptions("gpt-5.1"));
        Assert.Equal(["none", "low", "medium", "high", "xhigh"], OpenAiGenerationSettings.ReasoningEffortOptions("gpt-5.1-codex-max"));
    }
}

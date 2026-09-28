using System.Text.Json;

namespace Codev.Tests;

public sealed class OllamaRequestOptionsTests
{
    [Fact]
    public void Omits_options_when_the_model_default_is_requested()
    {
        Assert.Null(OllamaRequestOptions.Build(0, null));
    }

    [Fact]
    public void Emits_only_user_selected_num_ctx_and_temperature_options()
    {
        var options = OllamaRequestOptions.Build(32_768, 0.35);

        Assert.NotNull(options);
        Assert.Equal(32_768, options["num_ctx"]);
        Assert.Equal(0.35, options["temperature"]);
        Assert.Contains("\"temperature\":0.35", JsonSerializer.Serialize(options));
    }

    [Fact]
    public void Emits_only_valid_user_selected_sampling_options_and_token_budget()
    {
        var options = OllamaRequestOptions.Build(0, 0.2, 0.8, 30, 0.5, 1.1, 2048);

        Assert.NotNull(options);
        Assert.Equal(0.2, options["temperature"]);
        Assert.Equal(0.8, options["top_p"]);
        Assert.Equal(30, options["top_k"]);
        Assert.Equal(0.5, options["presence_penalty"]);
        Assert.Equal(1.1, options["repeat_penalty"]);
        Assert.Equal(2048, options["num_predict"]);
    }

    [Fact]
    public void Invalid_sampling_values_are_omitted_so_model_defaults_apply()
    {
        var options = OllamaRequestOptions.Build(0, 3, -0.1, 0, 4, double.NaN, 0);

        Assert.Null(options);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-0.1, null)]
    [InlineData(2.1, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    [InlineData(0.375, 0.38)]
    public void Normalizes_temperature_to_supported_finite_range(double? value, double? expected) =>
        Assert.Equal(expected, ConversationSamplingSettings.Normalize(value));

    [Theory]
    [InlineData(-0.1, null)]
    [InlineData(1.01, null)]
    [InlineData(0.444, 0.44)]
    public void Normalizes_probability_settings(double? value, double? expected) =>
        Assert.Equal(expected, ConversationSamplingSettings.NormalizeProbability(value));

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 1)]
    [InlineData(1001, null)]
    public void Normalizes_top_k(int? value, int? expected) =>
        Assert.Equal(expected, ConversationSamplingSettings.NormalizeTopK(value));
}

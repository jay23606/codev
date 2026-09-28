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

    [Theory]
    [InlineData(null, null)]
    [InlineData(-0.1, null)]
    [InlineData(2.1, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    [InlineData(0.375, 0.38)]
    public void Normalizes_temperature_to_supported_finite_range(double? value, double? expected) =>
        Assert.Equal(expected, ConversationSamplingSettings.Normalize(value));
}

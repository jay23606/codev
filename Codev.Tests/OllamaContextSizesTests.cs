namespace Codev.Tests;

public sealed class OllamaContextSizesTests
{
    [Theory]
    [InlineData("devstral-small-2-64k:latest", 65_536)]
    [InlineData("qwen3-coder:30b", 65_536)]
    [InlineData("qwen3-coder-next-q2-24k", 24_576)]
    [InlineData("hf.co/bartowski/Qwen_Qwen3-Coder-Next-GGUF:Q2_K_L", 24_576)]
    public void Limits_choices_to_the_configured_model_cap(string model, int expectedMaximum)
    {
        Assert.Equal(expectedMaximum, OllamaContextSizes.MaximumFor(model));
        Assert.Equal(0, OllamaContextSizes.ForModel(model)[0]);
        Assert.All(OllamaContextSizes.ForModel(model), size => Assert.True(size == 0 || size <= expectedMaximum));
    }
}

namespace Codev.Tests;

public sealed class OllamaModelDisplayNameTests
{
    [Theory]
    [InlineData("qwen3.8:27b", "Qwen3.8 · 27B")]
    [InlineData("qwen3-coder-next-q2-24k:latest", "Qwen3 coder next q2 24k")]
    [InlineData("hf.co/bartowski/Qwen_Coder-GGUF:Q4_K_M", "Qwen Coder · Q4_K_M")]
    public void Formats_arbitrary_installed_model_names(string model, string expected) =>
        Assert.Equal(expected, OllamaModelDisplayName.Format(model));
}

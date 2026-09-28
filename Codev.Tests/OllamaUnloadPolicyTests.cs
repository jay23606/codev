namespace Codev.Tests;

public sealed class OllamaUnloadPolicyTests
{
    [Theory]
    [InlineData(true, false, false, "active response")]
    [InlineData(false, true, false, "queued requests")]
    [InlineData(false, false, true, "model loading")]
    public void Blocks_unload_while_ollama_is_busy(bool generating, bool queued, bool loading, string expected)
    {
        var reason = OllamaUnloadPolicy.GetBlockingReason(generating, queued, loading);

        Assert.Contains(expected, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_unload_when_generation_and_queue_are_idle()
    {
        Assert.Null(OllamaUnloadPolicy.GetBlockingReason(false, false, false));
    }
}

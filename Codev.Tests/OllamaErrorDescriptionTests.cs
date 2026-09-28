namespace Codev.Tests;

public sealed class OllamaErrorDescriptionTests
{
    [Fact]
    public void Explains_repeat_limit_errors_as_generation_failures()
    {
        var result = OllamaErrorDescription.Describe(new InvalidOperationException("prediction aborted, token repeat limit reached"));

        Assert.Contains("server is reachable", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repeat limit", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Could not connect", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identifies_connection_failures_separately()
    {
        var result = OllamaErrorDescription.Describe(new HttpRequestException("Connection refused"));

        Assert.Contains("Could not connect to Ollama", result);
    }
}

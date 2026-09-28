namespace Codev.Tests;

public sealed class OllamaEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434/")]
    [InlineData("http://localhost:11434/ollama/", "http://localhost:11434/ollama/")]
    [InlineData("https://ollama.example/api/", "https://ollama.example/api/")]
    public void Normalizes_base_urls_and_preserves_optional_path(string input, string expected)
    {
        Assert.True(OllamaEndpoint.TryParse(input, out var endpoint, out var error), error);
        Assert.Equal(expected, endpoint.ToString());
    }

    [Fact]
    public void Builds_api_uri_beneath_a_configured_path()
    {
        Assert.True(OllamaEndpoint.TryParse("http://localhost:11434/ollama", out var endpoint, out _));

        Assert.Equal("http://localhost:11434/ollama/api/chat", OllamaEndpoint.ApiUri(endpoint, "api/chat").ToString());
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://ollama.lan:11434", false)]
    [InlineData("https://ollama.example", false)]
    public void Identifies_loopback_hosts_for_the_remote_endpoint_warning(string value, bool expectedLocal)
    {
        Assert.True(OllamaEndpoint.TryParse(value, out var endpoint, out _));
        Assert.Equal(expectedLocal, OllamaEndpoint.IsLoopback(endpoint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ollama.local:11434")]
    [InlineData("file:///tmp/ollama")]
    [InlineData("http://user:secret@localhost:11434")]
    [InlineData("http://localhost:11434?token=secret")]
    [InlineData("http://localhost:11434#frag")]
    public void Rejects_invalid_endpoints_or_embedded_credentials(string value) =>
        Assert.False(OllamaEndpoint.TryParse(value, out _, out _));
}

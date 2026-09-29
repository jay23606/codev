using Codev;

namespace Codev.Tests;

public sealed class ModelRequestErrorDescriptionTests
{
    [Fact]
    public void Handles_openai_request_timeouts_as_visible_model_failures()
    {
        var exception = new TimeoutException("OpenAI Code task request timed out after 5 minutes.");

        Assert.True(ModelRequestErrorDescription.IsHandledRequestFailure(exception));
        Assert.Equal("OpenAI API request failed. OpenAI Code task request timed out after 5 minutes.",
            ModelRequestErrorDescription.Describe(CloudModelProviders.OpenAI, exception));
    }

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, "OpenAI API request failed.")]
    [InlineData(CloudModelProviders.Anthropic, "Anthropic API request failed.")]
    public void Hosted_errors_name_the_provider_without_mentioning_ollama(string provider, string expectedPrefix)
    {
        var result = ModelRequestErrorDescription.Describe(provider, new HttpRequestException("quota limit reached"));

        Assert.StartsWith(expectedPrefix, result, StringComparison.Ordinal);
        Assert.Contains("quota limit reached", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Ollama", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_errors_keep_the_ollama_specific_description()
    {
        var result = ModelRequestErrorDescription.Describe("ollama", new HttpRequestException("Connection refused"));

        Assert.Contains("Could not connect to Ollama", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, "OpenAI returned an empty response.")]
    [InlineData(CloudModelProviders.Anthropic, "Anthropic returned an empty response.")]
    [InlineData("ollama", "installed and running in Ollama")]
    public void Empty_response_message_matches_provider(string provider, string expected)
    {
        var result = ModelRequestErrorDescription.EmptyResponse(provider);

        Assert.Contains(expected, result, StringComparison.Ordinal);
    }
}

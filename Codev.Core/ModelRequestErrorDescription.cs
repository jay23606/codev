namespace Codev;

/// <summary>Describes failed or empty model responses without confusing hosted APIs with local Ollama.</summary>
public static class ModelRequestErrorDescription
{
    public static string Describe(string provider, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (provider == CloudModelProviders.OpenAI) return $"OpenAI API request failed. {exception.Message.Trim()}";
        if (provider == CloudModelProviders.Anthropic) return $"Anthropic API request failed. {exception.Message.Trim()}";
        return OllamaErrorDescription.Describe(exception);
    }

    public static string EmptyResponse(string provider) => provider switch
    {
        CloudModelProviders.OpenAI => "OpenAI returned an empty response. Check the selected model and API response details.",
        CloudModelProviders.Anthropic => "Anthropic returned an empty response. Check the selected model and API response details.",
        _ => "The model returned an empty response. Check that the selected model is installed and running in Ollama."
    };
}

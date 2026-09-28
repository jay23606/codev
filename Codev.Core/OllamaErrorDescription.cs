namespace Codev;

public static class OllamaErrorDescription
{
    public static string Describe(Exception exception)
    {
        var message = exception.Message.Trim();
        if (message.Contains("prediction aborted, token repeat limit reached", StringComparison.OrdinalIgnoreCase))
            return "Ollama stopped generation after the model hit its token-repeat limit. The server is reachable. Try again, shorten the prompt, or switch models.";
        if (exception is HttpRequestException)
            return $"Could not connect to Ollama at http://127.0.0.1:11434. {message}";
        if (message.Contains("model", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return $"The selected model is not available in Ollama. {message}";
        return $"Ollama could not complete the request. {message}";
    }
}

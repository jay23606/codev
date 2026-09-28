namespace Codev;

/// <summary>Builds the supported Ollama options without overriding model defaults.</summary>
public static class OllamaRequestOptions
{
    public static Dictionary<string, object>? Build(int numCtx, double? temperature)
    {
        var options = new Dictionary<string, object>();
        if (numCtx > 0) options["num_ctx"] = numCtx;
        if (ConversationSamplingSettings.Normalize(temperature) is double normalized) options["temperature"] = normalized;
        return options.Count == 0 ? null : options;
    }
}

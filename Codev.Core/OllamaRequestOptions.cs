namespace Codev;

/// <summary>Builds the supported Ollama options without overriding model defaults.</summary>
public static class OllamaRequestOptions
{
    public static Dictionary<string, object>? Build(int numCtx, double? temperature)
        => Build(numCtx, temperature, null, null, null, null, null);

    public static Dictionary<string, object>? Build(int numCtx, double? temperature, double? topP,
        int? topK, double? presencePenalty, double? repeatPenalty, int? numPredict)
    {
        var options = new Dictionary<string, object>();
        if (numCtx > 0) options["num_ctx"] = numCtx;
        if (ConversationSamplingSettings.NormalizeTemperature(temperature) is double normalizedTemperature) options["temperature"] = normalizedTemperature;
        if (ConversationSamplingSettings.NormalizeProbability(topP) is double normalizedTopP) options["top_p"] = normalizedTopP;
        if (ConversationSamplingSettings.NormalizeTopK(topK) is int normalizedTopK) options["top_k"] = normalizedTopK;
        if (ConversationSamplingSettings.NormalizePenalty(presencePenalty) is double normalizedPresence) options["presence_penalty"] = normalizedPresence;
        if (ConversationSamplingSettings.NormalizePenalty(repeatPenalty) is double normalizedRepeat) options["repeat_penalty"] = normalizedRepeat;
        if (ConversationSamplingSettings.NormalizeOutputTokens(numPredict) is int normalizedPredict) options["num_predict"] = normalizedPredict;
        return options.Count == 0 ? null : options;
    }
}

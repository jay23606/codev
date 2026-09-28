namespace Codev;

public static class OllamaUnloadPolicy
{
    public static string? GetBlockingReason(bool isGenerating, bool hasQueuedRequests, bool isLoadingModel)
    {
        if (isGenerating) return "Wait for the active response to finish before unloading a model.";
        if (hasQueuedRequests) return "Wait for queued requests to finish before unloading a model.";
        if (isLoadingModel) return "Wait for model loading to finish before unloading a model.";
        return null;
    }
}

namespace Codev;

/// <summary>Creates a local, human-readable snapshot of the settings that will govern a conversation.</summary>
public static class ConversationStatusReport
{
    public static string Build(Conversation conversation, bool isGenerating, int queuedTurns, bool queuePaused, bool hostedRequestsEnabled)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var provider = conversation.Provider switch
        {
            CloudModelProviders.OpenAI => "OpenAI API",
            CloudModelProviders.Anthropic => "Anthropic API",
            _ => "Ollama (local)"
        };
        var context = CloudModelProviders.IsCloud(conversation.Provider)
            ? "managed by provider"
            : conversation.NumCtx > 0 ? $"{conversation.NumCtx:N0} tokens" : "model default";
        var temperature = conversation.Temperature is double value ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "model default";
        var project = string.IsNullOrWhiteSpace(conversation.ProjectPath) ? "not attached" : conversation.ProjectPath;
        var projectContext = string.IsNullOrWhiteSpace(conversation.ProjectPath)
            ? "none"
            : conversation.ContextFiles.Count > 0
                ? $"{conversation.ContextFiles.Count} selected file(s)"
                : "bounded source files included automatically";
        if (CloudModelProviders.IsCloud(conversation.Provider) && !conversation.IncludeProjectContextForHosted)
            projectContext = "excluded from hosted requests";

        var queue = queuePaused
            ? $"paused · {queuedTurns} saved turn(s)"
            : queuedTurns > 0
                ? $"{queuedTurns} queued turn(s)"
                : isGenerating ? "response in progress" : "idle";

        return string.Join('\n',
            $"Model: {provider} · {conversation.Model}",
            $"Context window: {context}",
            $"Temperature: {temperature}",
            $"Mode: {(conversation.IsPlanMode ? "Plan" : "Chat")}",
            $"Project: {project}",
            $"Project context: {projectContext}",
            $"Queue: {queue}",
            $"Hosted requests: {(CloudModelProviders.IsCloud(conversation.Provider) ? hostedRequestsEnabled ? "enabled for this session" : "disabled" : "not in use")}",
            "Tools and file changes: unavailable in Avalonia chat mode");
    }
}

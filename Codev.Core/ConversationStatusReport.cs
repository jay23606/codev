namespace Codev;

/// <summary>Creates a local, human-readable snapshot of the settings that will govern a conversation.</summary>
public static class ConversationStatusReport
{
    public static string Build(Conversation conversation, bool isGenerating, int queuedTurns, bool queuePaused,
        bool hostedRequestsEnabled, bool projectFolderTrusted = false, string? projectFolderTrustRoot = null,
        string? ollamaEndpoint = null, bool ollamaEndpointIsLocal = true)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var provider = conversation.Provider switch
        {
            CloudModelProviders.OpenAI => "OpenAI API",
            CloudModelProviders.Anthropic => "Anthropic API",
            _ => ollamaEndpointIsLocal ? "Ollama (local)" : $"Ollama (remote · {ollamaEndpoint})"
        };
        var context = CloudModelProviders.IsCloud(conversation.Provider)
            ? "managed by provider"
            : conversation.NumCtx > 0 ? $"{conversation.NumCtx:N0} tokens" : "model default";
        var temperature = conversation.Temperature is double value ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "model default";
        var project = string.IsNullOrWhiteSpace(conversation.ProjectPath) ? "not attached" : FolderName(conversation.ProjectPath);
        var projectContext = string.IsNullOrWhiteSpace(conversation.ProjectPath)
            ? "none"
            : conversation.ContextFiles.Count > 0
                ? $"{conversation.ContextFiles.Count} selected file(s)"
                : projectFolderTrusted ? "bounded source files included automatically" : "automatic context off for untrusted folder";
        if (CloudModelProviders.IsCloud(conversation.Provider) && !conversation.IncludeProjectContextForHosted)
            projectContext = "excluded from hosted requests";

        var queue = queuePaused
            ? $"paused · {queuedTurns} saved turn(s)"
            : queuedTurns > 0
                ? $"{queuedTurns} queued turn(s)"
                : isGenerating ? "response in progress" : "idle";
        var folderTrust = string.IsNullOrWhiteSpace(conversation.ProjectPath)
            ? "not applicable"
            : !projectFolderTrusted ? "untrusted"
            : projectFolderTrustRoot is null ? "trusted" : $"trusted · {FolderName(projectFolderTrustRoot)}";
        var canIncludeRepoMap = !string.IsNullOrWhiteSpace(conversation.ProjectPath) &&
            (conversation.ContextFiles.Count > 0 || projectFolderTrusted) &&
            (!CloudModelProviders.IsCloud(conversation.Provider) || conversation.IncludeProjectContextForHosted);

        return string.Join('\n',
            $"Model: {provider} · {conversation.Model}",
            $"Context window: {context}",
            $"Temperature: {temperature}",
            $"Mode: {(conversation.IsCodeTask ? "Code task" : conversation.IsPlanMode ? "Plan" : "Chat")}",
            $"Project: {project}",
            $"Folder trust: {folderTrust}",
            $"Project context: {projectContext}",
            $"Repository map: {(!conversation.IncludeRepoMap ? "off" : canIncludeRepoMap ? "included" : "unavailable under the current context policy")}",
            $"Queue: {queue}",
            $"Hosted requests: {(CloudModelProviders.IsCloud(conversation.Provider) ? hostedRequestsEnabled ? "enabled for this session" : "disabled" : "not in use")}",
            $"Tools and file changes: {(conversation.IsCodeTask ? "available with per-change and per-command approval" : "unavailable outside Code task mode")}");
    }

    private static string FolderName(string path)
    {
        var name = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\')
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(name) || name.EndsWith(':') ? "filesystem root" : name;
    }
}

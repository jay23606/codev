namespace Codev;

/// <summary>Creates a local, human-readable snapshot of the settings that will govern a conversation.</summary>
public static class ConversationStatusReport
{
    public static string Build(Conversation conversation, bool isGenerating, int queuedTurns, bool queuePaused,
        bool hostedRequestsEnabled, bool projectFolderTrusted = false, string? projectFolderTrustRoot = null,
        string? ollamaEndpoint = null, bool ollamaEndpointIsLocal = true,
        IReadOnlyList<string>? lastPromptInstructionFiles = null,
        ProjectCommandPermissionMode commandPermissionMode = ProjectCommandPermissionMode.AskEveryTime,
        int allowedCommandRules = 0, int deniedCommandRules = 0,
        bool? automaticProjectContextIncluded = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var provider = conversation.Provider switch
        {
            CloudModelProviders.OpenAI => "OpenAI API",
            CloudModelProviders.Anthropic => "Anthropic API",
            _ => ollamaEndpointIsLocal ? "Ollama (local)" : $"Ollama (remote · {RemoteEndpointHost(ollamaEndpoint)})"
        };
        var context = CloudModelProviders.IsCloud(conversation.Provider)
            ? "managed by provider"
            : conversation.NumCtx > 0 ? $"{conversation.NumCtx:N0} tokens" : "model default";
        var temperature = conversation.Temperature is double value ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "model default";
        var sampling = $"temperature={temperature}, top_p={Format(conversation.TopP)}, top_k={conversation.TopK?.ToString() ?? "model default"}, presence_penalty={Format(conversation.PresencePenalty)}, repeat_penalty={Format(conversation.RepeatPenalty)}, num_predict={conversation.NumPredict?.ToString() ?? "model default"}";
        var project = string.IsNullOrWhiteSpace(conversation.ProjectPath) ? "not attached" : FolderName(conversation.ProjectPath);
        var projectContext = string.IsNullOrWhiteSpace(conversation.ProjectPath)
            ? "none"
            : conversation.ContextFiles.Count > 0
                ? $"{conversation.ContextFiles.Count} selected file(s)"
                : projectFolderTrusted
                    ? automaticProjectContextIncluded == false ? "automatic source context off for this mode" : "bounded source files included automatically"
                    : "automatic context off for untrusted folder";
        if (CloudModelProviders.IsCloud(conversation.Provider) && !conversation.IncludeProjectContextForHosted)
            projectContext = "excluded from hosted requests";

        var queue = queuePaused
            ? $"paused · {queuedTurns} saved turn(s)"
            : queuedTurns > 0
                ? $"{queuedTurns} queued turn(s)"
                : isGenerating ? "response in progress" : "idle";
        var hostedCodeTaskConsent = conversation.Provider == CloudModelProviders.OpenAI
            ? conversation.AllowHostedCodeTask ? "granted for this conversation" : "not granted"
            : "not applicable";
        var folderTrust = string.IsNullOrWhiteSpace(conversation.ProjectPath)
            ? "not applicable"
            : !projectFolderTrusted ? "untrusted"
            : projectFolderTrustRoot is null ? "trusted" : $"trusted · {FolderName(projectFolderTrustRoot)}";
        var canIncludeRepoMap = !string.IsNullOrWhiteSpace(conversation.ProjectPath) &&
            (conversation.ContextFiles.Count > 0 || projectFolderTrusted) &&
            (!CloudModelProviders.IsCloud(conversation.Provider) || conversation.IncludeProjectContextForHosted);
        var lastPromptUsageMatches = conversation.LastPromptModel.Equals(conversation.Model, StringComparison.OrdinalIgnoreCase) &&
            conversation.LastPromptProvider.Equals(conversation.Provider, StringComparison.OrdinalIgnoreCase);
        var lastPromptUsage = lastPromptUsageMatches && (conversation.LastPromptTokens > 0 || conversation.LastPromptOutputTokens is not null)
            ? CloudModelProviders.IsCloud(conversation.Provider)
                ? conversation.LastPromptTokens > 0 && conversation.LastPromptOutputTokens is { } outputTokens
                    ? $"{conversation.LastPromptTokens:N0} input · {outputTokens:N0} output tokens (provider-reported)"
                    : conversation.LastPromptTokens > 0
                        ? $"{conversation.LastPromptTokens:N0} provider-reported input tokens"
                        : $"{conversation.LastPromptOutputTokens:N0} provider-reported output tokens"
                : conversation.LastPromptContext > 0
                    ? $"{conversation.LastPromptTokens:N0} input tokens · {Math.Round(100d * conversation.LastPromptTokens / conversation.LastPromptContext):N0}% of {conversation.LastPromptContext:N0} tokens"
                    : $"{conversation.LastPromptTokens:N0} input tokens · context usage unavailable"
            : "not available for the selected model yet";
        var compaction = string.IsNullOrWhiteSpace(conversation.CompactionSummary)
            ? "off"
            : conversation.CompactionFromMessageCount == 0
                ? $"active · first {Math.Max(0, conversation.CompactionThroughMessageCount):N0} messages summarized for future prompts"
                : $"active · messages {conversation.CompactionFromMessageCount + 1:N0}–{Math.Max(0, conversation.CompactionThroughMessageCount):N0} summarized for future prompts";
        var instructionFiles = string.IsNullOrWhiteSpace(conversation.ProjectPath)
            ? "none · no project attached"
            : CloudModelProviders.IsCloud(conversation.Provider) && !conversation.IncludeProjectContextForHosted
                ? "excluded from hosted requests"
                : lastPromptInstructionFiles is null
                    ? "not captured for this session"
                    : lastPromptInstructionFiles.Count == 0
                        ? "none in the last captured request"
                        : string.Join(", ", lastPromptInstructionFiles);

        return string.Join('\n',
            $"Model: {provider} · {conversation.Model}",
            $"Context window: {context}",
            $"Last request usage: {lastPromptUsage}",
            $"Conversation summary: {compaction}",
            $"Temperature: {temperature}",
            $"Thinking: {(CloudModelProviders.IsCloud(conversation.Provider) ? "unavailable for hosted providers" : conversation.ThinkEnabled ? "requested" : "off")}",
            $"Sampling overrides: {(conversation.Provider == "ollama" ? sampling : "unavailable for hosted providers")}",
            $"OpenAI reasoning effort: {(conversation.Provider == CloudModelProviders.OpenAI && OpenAiGenerationSettings.SupportsReasoningControls(conversation.Model) ? OpenAiGenerationSettings.NormalizeEffort(conversation.OpenAiReasoningEffort, conversation.Model) ?? "model default" : "unavailable for this model")}",
            $"OpenAI response verbosity: {(conversation.Provider == CloudModelProviders.OpenAI && OpenAiGenerationSettings.SupportsReasoningControls(conversation.Model) ? OpenAiGenerationSettings.NormalizeVerbosity(conversation.OpenAiVerbosity) ?? "model default" : "unavailable for this model")}",
            $"Mode: {(conversation.IsCodeTask ? "Code task" : conversation.IsPlanMode ? "Plan" : "Chat")}",
            $"OpenAI Code task consent: {hostedCodeTaskConsent}",
            $"Project: {project}",
            $"Folder trust: {folderTrust}",
            $"Project context: {projectContext}",
            $"Project instruction files (last request): {instructionFiles}",
            $"Repository map: {(!conversation.IncludeRepoMap ? "off" : canIncludeRepoMap ? "included" : "unavailable under the current context policy")}",
            $"Queue: {queue}",
            $"Hosted requests: {(CloudModelProviders.IsCloud(conversation.Provider) ? hostedRequestsEnabled ? "enabled for this session" : "disabled" : "not in use")}",
            $"Tools and file changes: {(conversation.IsCodeTask ? "available with per-change review and project command permissions" : "unavailable outside Code task mode")}",
            $"Project command permissions: {(commandPermissionMode switch
            {
                ProjectCommandPermissionMode.Allowlist => $"exact allowlist · {allowedCommandRules} allow rule(s), {deniedCommandRules} deny rule(s); unlisted commands ask",
                ProjectCommandPermissionMode.ReadOnly => $"read-only classifier · {deniedCommandRules} saved deny rule(s); unrecognized commands ask",
                _ => $"ask every time · {deniedCommandRules} saved deny rule(s)"
            })}");
    }

    private static string FolderName(string path)
    {
        var name = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\')
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(name) || name.EndsWith(':') ? "filesystem root" : name;
    }

    private static string RemoteEndpointHost(string? endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.Trim('[', ']');
            if (uri.HostNameType == UriHostNameType.IPv6) host = $"[{host}]";
            var defaultPort = uri.IsDefaultPort;
            var port = defaultPort ? "" : $":{uri.Port}";
            return $"{uri.Scheme}://{host}{port}";
        }
        return "configured remote host";
    }

    private static string Format(double? value) => value is double number
        ? number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        : "model default";
}

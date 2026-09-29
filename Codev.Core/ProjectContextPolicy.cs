namespace Codev;

public static class ProjectContextPolicy
{
    public static bool CanRunCodeTask(string provider, bool hostedProviderConnected, bool isLoopbackOllama,
        string? projectPath, bool projectFolderTrusted) => provider switch
    {
        CloudModelProviders.OpenAI => hostedProviderConnected,
        "ollama" => isLoopbackOllama && !string.IsNullOrWhiteSpace(projectPath) && projectFolderTrusted,
        _ => false
    };

    public static bool CanSendCodeTask(string provider, bool hostedProviderConnected, bool hostedCodeTaskApproved,
        bool isLoopbackOllama, string? projectPath, bool projectFolderTrusted) =>
        CanRunCodeTask(provider, hostedProviderConnected, isLoopbackOllama, projectPath, projectFolderTrusted) &&
        (provider != CloudModelProviders.OpenAI || hostedCodeTaskApproved);

    public static string? GetProjectPathForQueuedTurn(string? projectPath, bool hasExplicitFiles, bool projectFolderTrusted) =>
        hasExplicitFiles || projectFolderTrusted ? projectPath : null;

    public static bool ShouldInclude(string? projectPath, string provider, bool hostedContextOptIn,
        bool hasExplicitFiles, bool projectFolderTrusted) =>
        !string.IsNullOrWhiteSpace(projectPath) &&
        (provider == "ollama" || hostedContextOptIn) &&
        (hasExplicitFiles || projectFolderTrusted);
}

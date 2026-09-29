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

    public static string? GetProjectPathForQueuedTurn(string? projectPath, bool hasExplicitFiles, bool projectFolderTrusted) =>
        hasExplicitFiles || projectFolderTrusted ? projectPath : null;

    public static bool ShouldInclude(string? projectPath, string provider, bool hostedContextOptIn,
        bool hasExplicitFiles, bool projectFolderTrusted) =>
        !string.IsNullOrWhiteSpace(projectPath) &&
        (provider == "ollama" || hostedContextOptIn) &&
        (hasExplicitFiles || projectFolderTrusted);
}

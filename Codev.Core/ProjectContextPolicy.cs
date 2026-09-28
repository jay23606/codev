namespace Codev;

public static class ProjectContextPolicy
{
    public static string? GetProjectPathForQueuedTurn(string? projectPath, bool hasExplicitFiles, bool projectFolderTrusted) =>
        hasExplicitFiles || projectFolderTrusted ? projectPath : null;

    public static bool ShouldInclude(string? projectPath, string provider, bool hostedContextOptIn,
        bool hasExplicitFiles, bool projectFolderTrusted) =>
        !string.IsNullOrWhiteSpace(projectPath) &&
        (provider == "ollama" || hostedContextOptIn) &&
        (hasExplicitFiles || projectFolderTrusted);
}

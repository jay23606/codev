namespace Codev;

public static class ProjectContextEstimateLabel
{
    public static string ForProject(string? projectPath, string provider, bool includeProjectContextForHosted,
        bool projectFolderTrusted, IReadOnlyList<string>? selectedFiles)
    {
        var projectAvailable = !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath);
        var hostedProvider = CloudModelProviders.IsCloud(provider);
        if (!projectAvailable || (hostedProvider && !includeProjectContextForHosted))
            return Format(projectAvailable, hostedProvider, includeProjectContextForHosted, 0);
        if (!projectFolderTrusted && selectedFiles is not { Count: > 0 })
            return "Untrusted folder · automatic project context is off.";

        try
        {
            var estimatedTokens = new WorkspaceFileService(projectPath!).EstimateContextTokens(selectedFiles);
            var label = Format(true, hostedProvider, includeProjectContextForHosted, estimatedTokens);
            return projectFolderTrusted ? label + " Trusted project guidance and matching path rules may add to this estimate." : label;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return "Project context estimate unavailable.";
        }
    }

    public static string Format(bool projectAvailable, bool hostedProvider, bool includeProjectContextForHosted, int estimatedTokens)
    {
        if (!projectAvailable) return "No project files will be included.";
        if (hostedProvider && !includeProjectContextForHosted)
            return "Project files stay local; hosted context is off.";
        return $"Project files: ≈{Math.Max(0, estimatedTokens):N0} tokens · excludes conversation history.";
    }
}

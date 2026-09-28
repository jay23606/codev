namespace Codev;

public static class ProjectContextEstimateLabel
{
    public static string ForProject(string? projectPath, string provider, bool includeProjectContextForHosted, IReadOnlyList<string>? selectedFiles)
    {
        var projectAvailable = !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath);
        var hostedProvider = CloudModelProviders.IsCloud(provider);
        if (!projectAvailable || (hostedProvider && !includeProjectContextForHosted))
            return Format(projectAvailable, hostedProvider, includeProjectContextForHosted, 0);

        try
        {
            var estimatedTokens = new WorkspaceFileService(projectPath!).EstimateContextTokens(selectedFiles);
            return Format(true, hostedProvider, includeProjectContextForHosted, estimatedTokens);
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

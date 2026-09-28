namespace Codev;

public static class ProjectContextEstimateLabel
{
    public static string Format(bool projectAvailable, bool hostedProvider, bool includeProjectContextForHosted, int estimatedTokens)
    {
        if (!projectAvailable) return "No project files will be included.";
        if (hostedProvider && !includeProjectContextForHosted)
            return "Project files stay local; hosted context is off.";
        return $"Project files: ≈{Math.Max(0, estimatedTokens):N0} tokens · excludes conversation history.";
    }
}

namespace Codev;

public static class ProjectConversationResume
{
    public static Conversation? FindMostRecent(IEnumerable<Conversation> conversations, string projectPath, Guid? excludeConversationId = null)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var targetPath = NormalizePath(projectPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return conversations
            .Where(conversation => !conversation.IsArchived && conversation.Id != excludeConversationId &&
                                   !string.IsNullOrWhiteSpace(conversation.ProjectPath) &&
                                   PathsEqual(conversation.ProjectPath, targetPath, comparison))
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .FirstOrDefault();
    }

    public static bool PathsEqual(string first, string second, StringComparison comparison)
    {
        try { return string.Equals(NormalizePath(first), NormalizePath(second), comparison); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

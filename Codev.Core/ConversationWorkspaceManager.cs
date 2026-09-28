namespace Codev;

/// <summary>Creates one stable, private workspace directory for each conversation.</summary>
public sealed class ConversationWorkspaceManager
{
    private readonly string _root;
    private readonly string _codevRoot;

    public ConversationWorkspaceManager(string localApplicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);
        _codevRoot = Path.GetFullPath(Path.Combine(localApplicationDataPath, "Codev"));
        _root = Path.Combine(_codevRoot, "workspaces");
    }

    public string GetWorkspacePath(Guid conversationId)
    {
        if (conversationId == Guid.Empty) throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
        return Path.Combine(_root, $"session-{conversationId:N}");
    }

    public string GetOrCreateWorkspace(Guid conversationId)
    {
        Directory.CreateDirectory(_codevRoot);
        if ((File.GetAttributes(_codevRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Codev data folder cannot be a symbolic link or junction for generated workspaces.");
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Codev workspaces folder cannot be a symbolic link or junction.");
        var path = GetWorkspacePath(conversationId);
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The conversation workspace cannot be a symbolic link or junction.");
        return path;
    }

    public bool IsManagedWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.Equals(parent, _root, PathComparison)) return false;
            var name = Path.GetFileName(fullPath);
            return name.StartsWith("session-", StringComparison.Ordinal) && Guid.TryParseExact(name[8..], "N", out _);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

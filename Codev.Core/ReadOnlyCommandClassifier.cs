namespace Codev;

/// <summary>
/// A deliberately small shell-command allowlist for the optional read-only approval mode.
/// It accepts only one simple command, plain relative paths, and known inspection verbs.
/// Anything the parser cannot prove safe remains subject to normal user approval.
/// </summary>
public static class ReadOnlyCommandClassifier
{
    private static readonly char[] ShellSyntax = ['\'', '"', '`', '$', '|', '&', ';', '<', '>', '(', ')', '{', '}', '*', '?', '[', ']', '\\', '#', '!'];
    private const int MaxReadBytes = 256 * 1024;
    private const int MaxListedEntries = 200;
    private const int MaxOutputCharacters = 8000;

    public static bool IsReadOnly(string command, string projectPath, string shellName, IReadOnlyList<string>? contextExclusions = null)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(projectPath) ||
            command.Length > 4000 || command.Any(character => char.IsControl(character) || character is '\u0085' or '\u2028' or '\u2029') ||
            command.IndexOfAny(ShellSyntax) >= 0 ||
            !ProjectCommandPermissionRegistry.CanCreateAllowRule(command)) return false;

        var parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var verb = parts[0];
        var isPowerShell = shellName.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
                           shellName.Contains("pwsh", StringComparison.OrdinalIgnoreCase);
        var isUnix = IsOneOf(shellName, "bash", "zsh", "sh", "fish");

        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root) || IsProtectedProjectPath(root) || HasReparsePoint(root)) return false;
        if (isPowerShell && IsOneOf(verb, "Get-Location", "pwd") && parts.Length == 1) return true;
        if (isUnix && IsOneOf(verb, "pwd") && parts.Length == 1) return true;

        var listCommand = isPowerShell
            ? IsOneOf(verb, "Get-ChildItem", "ls", "dir")
            : isUnix && IsOneOf(verb, "ls", "dir");
        var readCommand = isPowerShell
            ? IsOneOf(verb, "Get-Content", "cat", "type")
            : isUnix && IsOneOf(verb, "cat");
        if (!listCommand && !readCommand) return false;
        if (readCommand && !OperatingSystem.IsWindows()) return false;

        WorkspaceFileService workspace;
        try { workspace = new WorkspaceFileService(root, contextExclusions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        if (parts.Length == 1) return listCommand;

        foreach (var argument in parts.Skip(1))
        {
            if (!IsPlainRelativePath(argument)) return false;
            if (argument.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(WorkspaceFileService.IsIgnoredDirectory)) return false;
            if (workspace.IsContextExcluded(argument)) return false;
            string target;
            try { target = workspace.ResolvePath(argument, allowWorkspaceRoot: listCommand); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
            if (!IsWithinRoot(root, target) || HasReparsePointInPath(root, target)) return false;
            if (readCommand && !workspace.IsSupportedContextFile(argument)) return false;
            if (readCommand && !File.Exists(target)) return false;
            if (readCommand && !ReadOnlyFileHandleReader.CanRead(target, root, contextExclusions, MaxReadBytes)) return false;
            if (listCommand && !Directory.Exists(target) && !File.Exists(target)) return false;
        }
        return true;
    }

    /// <summary>Executes an already-classified inspection using bounded .NET file APIs, never a shell process.</summary>
    public static Task<string> ExecuteAsync(string command, string projectPath, string shellName, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? contextExclusions = null)
    {
        if (!IsReadOnly(command, projectPath, shellName, contextExclusions)) throw new InvalidOperationException("The command is not in the read-only command set.");
        var parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0];
        var isPowerShell = shellName.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) || shellName.Contains("pwsh", StringComparison.OrdinalIgnoreCase);
        var isUnix = IsOneOf(shellName, "bash", "zsh", "sh", "fish");
        var isLocation = isPowerShell ? IsOneOf(verb, "Get-Location", "pwd") : isUnix && IsOneOf(verb, "pwd");
        if (isLocation) return Task.FromResult(Path.GetFullPath(projectPath));

        var isList = isPowerShell ? IsOneOf(verb, "Get-ChildItem", "ls", "dir") : isUnix && IsOneOf(verb, "ls", "dir");
        var root = Path.GetFullPath(projectPath);
        var workspace = new WorkspaceFileService(root, contextExclusions);
        var output = new List<string>();
        var targets = parts.Length == 1 ? [root] : parts.Skip(1).Select(part => Path.GetFullPath(Path.Combine(root, part))).ToArray();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasReparsePointInPath(root, target)) throw new IOException("A project path changed to a symlink or reparse point; inspection stopped.");
            if (isList)
            {
                if (Directory.Exists(target))
                {
                    output.Add($"Directory: {Path.GetRelativePath(root, target)}");
                    var relative = Path.GetRelativePath(root, target);
                    output.AddRange(workspace.ListDirectoryEntries(relative == "." ? "" : relative, MaxListedEntries));
                }
                else output.Add(Path.GetFileName(target));
                continue;
            }

            var relativeFile = Path.GetRelativePath(root, target);
            if (!File.Exists(target) || HasReparsePoint(target) || workspace.IsContextExcluded(relativeFile))
                throw new IOException("The file is missing, too large, or changed to a symlink; inspection stopped.");
            output.Add($"==> {Path.GetRelativePath(root, target)} <==");
            output.Add(ReadOnlyFileHandleReader.ReadText(target, root, contextExclusions, MaxReadBytes));
        }
        var result = string.Join(Environment.NewLine, output);
        result = result.Length <= MaxOutputCharacters ? result : result[..MaxOutputCharacters] + "\n… [read-only inspection output truncated]";
        return Task.FromResult(result);
    }

    private static bool IsPlainRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '-' || Path.IsPathRooted(value)) return false;
        if (value.Contains(':') || value.Contains('~')) return false;
        if (value is "." or "./" or @".\") return true;
        return value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not ("." or "..") && segment.Length > 0 && segment[0] != '.');
    }

    private static bool IsWithinRoot(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool IsProtectedProjectPath(string root)
    {
        var localData = CodevDataPaths.LocalDataRoot;
        if (string.IsNullOrWhiteSpace(localData)) return false;
        var protectedRoot = Path.GetFullPath(Path.Combine(localData, "Codev"));
        var relative = Path.GetRelativePath(protectedRoot, root);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static bool HasReparsePointInPath(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0 || part == ".") continue;
            current = Path.Combine(current, part);
            if (HasReparsePoint(current)) return true;
        }
        return false;
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    private static bool IsOneOf(string value, params string[] options) =>
        options.Contains(value, StringComparer.OrdinalIgnoreCase);
}

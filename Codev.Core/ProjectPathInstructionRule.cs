using System.Text.RegularExpressions;
using System.Text;

namespace Codev;

public enum ProjectPathRuleActivation { MatchingFiles, Always }

public sealed record ProjectPathInstructionRule(string RelativePath, string Description, IReadOnlyList<string> Globs,
    string Instructions, ProjectPathRuleActivation Activation = ProjectPathRuleActivation.MatchingFiles);

/// <summary>Parses and matches explicitly path-scoped Markdown guidance from a trusted project.</summary>
public static class ProjectPathInstructionRuleParser
{
    public const int MaxDefinitionBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ManualMentionRegex = new(@"(?<![A-Za-z0-9_@])@rule:([a-z][a-z0-9_-]{0,39})(?![A-Za-z0-9_-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool TryParse(string relativePath, string contents, out ProjectPathInstructionRule? rule)
    {
        rule = null;
        if (contents.Length > MaxDefinitionBytes) return false;
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---") return false;
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0) return false;

        string? description = null;
        string? globsText = null;
        var activation = ProjectPathRuleActivation.MatchingFiles;
        foreach (var line in lines.Skip(1).Take(end - 1))
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            var colon = entry.IndexOf(':');
            if (colon <= 0) return false;
            var key = entry[..colon].Trim();
            var value = entry[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            else if (key.Equals("globs", StringComparison.OrdinalIgnoreCase)) globsText = value;
            else if (key.Equals("activation", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("always", StringComparison.OrdinalIgnoreCase)) activation = ProjectPathRuleActivation.Always;
                else if (value.Equals("files", StringComparison.OrdinalIgnoreCase)) activation = ProjectPathRuleActivation.MatchingFiles;
                else return false;
            }
            else return false;
        }

        var globs = (globsText ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var body = string.Join('\n', lines.Skip(end + 1)).Trim();
        if (string.IsNullOrWhiteSpace(description) || description.Length > 180 ||
            (activation == ProjectPathRuleActivation.MatchingFiles && globs.Length == 0) || globs.Length > 16 ||
            globs.Any(glob => !IsValidGlob(glob)) || string.IsNullOrWhiteSpace(body) || body.Length > 12_000)
            return false;

        rule = new ProjectPathInstructionRule(relativePath, description, globs, body, activation);
        return true;
    }

    public static bool AppliesTo(ProjectPathInstructionRule rule, string relativeFile)
    {
        var path = relativeFile.Replace('\\', '/').TrimStart('/');
        return rule.Globs.Any(glob => Matches(glob, path));
    }

    public static IReadOnlyList<string> FindManualMentions(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return [];
        return ManualMentionRegex.Matches(prompt).Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
    }

    public static IReadOnlyList<string> FindMentionSuggestions(WorkspaceFileService service, string prefix, int maxResults = 12)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (maxResults <= 0) return [];
        const string directory = ".codev/rules";
        try
        {
            if (service.IsContextExcluded(directory)) return [];
            var folder = service.ResolvePath(directory, allowWorkspaceRoot: true);
            if (!Directory.Exists(folder)) return [];
            var normalizedPrefix = prefix.StartsWith("rule:", StringComparison.OrdinalIgnoreCase) ? prefix[5..] : "";
            var suggestions = new List<string>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).Take(64))
            {
                var relative = Path.GetRelativePath(service.Root, path).Replace('\\', '/');
                if (service.IsContextExcluded(relative)) continue;
                try
                {
                    var name = Path.GetFileNameWithoutExtension(relative);
                    if (!Regex.IsMatch(name, @"^[a-z][a-z0-9_-]{0,39}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                    var contents = ReadDefinition(service, relative);
                    if (contents is null) continue;
                    if (!TryParse(relative, contents, out _)) continue;
                    var value = "rule:" + name;
                    if (value[5..].StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) suggestions.Add(value);
                    if (suggestions.Count >= maxResults) break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
            return suggestions;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { return []; }
    }

    public static string? ReadDefinition(WorkspaceFileService service, string relativePath)
    {
        var safePath = service.ResolvePath(relativePath);
        using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return DecodeBounded(stream);
    }

    public static async Task<string?> ReadDefinitionAsync(WorkspaceFileService service, string relativePath, CancellationToken cancellationToken = default)
    {
        var safePath = service.ResolvePath(relativePath);
        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxDefinitionBytes) return null;
        var bytes = new byte[MaxDefinitionBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(total, bytes.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxDefinitionBytes) return null;
        try { return StrictUtf8.GetString(bytes, 0, total); }
        catch (DecoderFallbackException) { return null; }
    }

    private static string? DecodeBounded(FileStream stream)
    {
        if (stream.Length > MaxDefinitionBytes) return null;
        var bytes = new byte[MaxDefinitionBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxDefinitionBytes) return null;
        try { return StrictUtf8.GetString(bytes, 0, total); }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool IsValidGlob(string glob)
    {
        var path = glob.Replace('\\', '/');
        return path.Length is > 0 and <= 240 && !Path.IsPathRooted(path) && !path.StartsWith('/') &&
               !Regex.IsMatch(path, @"^[A-Za-z]:", RegexOptions.CultureInvariant) &&
               !path.Split('/').Any(segment => segment is ".." or "" or ".");
    }

    private static bool Matches(string glob, string path)
    {
        var pattern = glob.Replace('\\', '/');
        var expression = new System.Text.StringBuilder("^");
        if (!pattern.Contains('/')) expression.Append("(?:.*/)?");
        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];
            if (current == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                    expression.Append("(?:.*/)?");
                }
                else expression.Append(".*");
            }
            else if (current == '*') expression.Append("[^/]*");
            else if (current == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(current.ToString()));
        }
        expression.Append('$');
        var caseOption = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.IsMatch(path, expression.ToString(), caseOption | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}

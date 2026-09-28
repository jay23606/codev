using System.Text.RegularExpressions;

namespace Codev;

public sealed record ProjectPathInstructionRule(string RelativePath, string Description, IReadOnlyList<string> Globs, string Instructions);

/// <summary>Parses and matches explicitly path-scoped Markdown guidance from a trusted project.</summary>
public static class ProjectPathInstructionRuleParser
{
    public static bool TryParse(string relativePath, string contents, out ProjectPathInstructionRule? rule)
    {
        rule = null;
        if (contents.Length > 16 * 1024) return false;
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---") return false;
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0) return false;

        string? description = null;
        string? globsText = null;
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
            else return false;
        }

        var globs = (globsText ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var body = string.Join('\n', lines.Skip(end + 1)).Trim();
        if (string.IsNullOrWhiteSpace(description) || description.Length > 180 || globs.Length == 0 || globs.Length > 16 ||
            globs.Any(glob => !IsValidGlob(glob)) || string.IsNullOrWhiteSpace(body) || body.Length > 12_000)
            return false;

        rule = new ProjectPathInstructionRule(relativePath, description, globs, body);
        return true;
    }

    public static bool AppliesTo(ProjectPathInstructionRule rule, string relativeFile)
    {
        var path = relativeFile.Replace('\\', '/').TrimStart('/');
        return rule.Globs.Any(glob => Matches(glob, path));
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

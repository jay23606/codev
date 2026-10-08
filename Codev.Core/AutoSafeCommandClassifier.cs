using System.Text.RegularExpressions;

namespace Codev;

/// <summary>Recognizes a narrow set of read-only Git/GitHub checks that Auto may run through the configured shell.</summary>
public static class AutoSafeCommandClassifier
{
    private const string JqValue = @"(?:html_url|status|source(?:\.(?:branch|path))?|build_type|public|https_enforced|cname)";
    private static readonly string JqFilter = $@"(?:\.{JqValue}|\{{\s*[A-Za-z_][A-Za-z0-9_]*\s*:\s*\.{JqValue}(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*\s*:\s*\.{JqValue})*\s*\}})";
    private const string GitHubSegment = @"(?!\.{1,2}(?:/|$))[A-Za-z0-9_.-]+";
    private static readonly Regex SafeClause = new(
        $@"^\s*(?:(?<git>git(?:\.exe)?)\s+(?:-C\s+(?<repo>[A-Za-z0-9_.\\/-]+)\s+)?(?:status(?:\s+--short)?|branch\s+--show-current|log\s+-1\s+--oneline)|gh(?:\.exe)?\s+auth\s+status(?:\s+--hostname\s+github\.com)?|gh(?:\.exe)?\s+api\s+repos/(?<owner>{GitHubSegment})/(?<repository>{GitHubSegment})/pages(?:\s+--jq\s+(?:'{JqFilter}'|""{JqFilter}""))?|(?:Start-Sleep\s+-Seconds\s+(?:[0-9]|[12][0-9]|30)|sleep\s+(?:[0-9]|[12][0-9]|30)))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ClauseSeparators = new(@"(?:&&|\|\||;)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GitClauseStart = new(
        @"(?<prefix>^\s*|&&\s*|\|\|\s*|;\s*)(?<git>git(?:\.exe)?)(?=\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Accepts only chains whose individual clauses are explicitly recognized as read-only checks.</summary>
    public static bool IsSafe(string? command) => IsSafe(command, projectPath: null);

    /// <summary>Recognizes the narrow command set and, when scoped, keeps relative Git -C directories inside the project without traversing links.</summary>
    public static bool IsSafe(string? command, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4000 ||
            command.Any(character => char.IsControl(character) && character != '\t' || character is '\u0085' or '\u2028' or '\u2029')) return false;

        var clauses = ClauseSeparators.Split(command);
        return clauses.Length is > 0 and <= 12 && clauses.All(rawClause =>
        {
            var clause = rawClause.Trim();
            return clause.Length > 0 &&
                !clause.Contains('|') && !clause.Contains('>') && !clause.Contains('<') &&
                !clause.Contains('$') && !clause.Contains('`') &&
                SafeClause.IsMatch(clause) && IsSafeRepositoryPath(clause) && IsProjectScopedRepositoryPath(clause, projectPath);
        });
    }

    /// <summary>
    /// Prevents an approved Git status query from invoking a repository-configured fsmonitor hook.
    /// Git status can execute the command configured by core.fsmonitor, so a syntactically read-only
    /// query is not read-only when it inherits that project setting.
    /// </summary>
    public static string PrepareApprovedExecutionCommand(string command, string? projectPath = null)
    {
        if (!IsSafe(command, projectPath)) return command;
        return GitClauseStart.Replace(command, match =>
            match.Groups["prefix"].Value + match.Groups["git"].Value + " -c core.fsmonitor=");
    }

    private static bool IsSafeRepositoryPath(string clause)
    {
        var match = SafeClause.Match(clause);
        if (!match.Success || !match.Groups["repo"].Success) return true;
        var path = match.Groups["repo"].Value.Replace('\\', '/');
        return !Path.IsPathRooted(path) && !path.Split('/').Any(part => part is ".." or "" || part.StartsWith(".", StringComparison.Ordinal));
    }

    private static bool IsProjectScopedRepositoryPath(string clause, string? projectPath)
    {
        var match = SafeClause.Match(clause);
        if (!match.Groups["git"].Success || string.IsNullOrWhiteSpace(projectPath)) return true;
        try
        {
            var root = Path.GetFullPath(projectPath);
            if (!Directory.Exists(root)) return false;
            var current = root;
            if (match.Groups["repo"].Success)
            {
                var relative = match.Groups["repo"].Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative)) return false;
                foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
                {
                    if (segment.Length == 0 || segment.StartsWith(".", StringComparison.Ordinal)) return false;
                    current = Path.Combine(current, segment);
                    if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                }
            }

            // Git walks upward when it does not find a repository marker. Do not let an attached
            // subfolder silently inspect a repository outside its trusted root.
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            while (current.Equals(root, comparison) || current.StartsWith(rootPrefix, comparison))
            {
                var marker = Path.Combine(current, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                    return (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0;
                if (current.Equals(root, comparison)) break;
                current = Path.GetDirectoryName(current)!;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

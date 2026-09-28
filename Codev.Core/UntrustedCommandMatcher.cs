using System.Text.RegularExpressions;

namespace Codev;

/// <summary>Finds likely command rewrites only when an action family and concrete target both match.</summary>
public static partial class UntrustedCommandMatcher
{
    private static readonly (Regex Pattern, string Family)[] Families =
    [
        (DeleteCommand(), "delete"),
        (NetworkCommand(), "network"),
        (SecretReadCommand(), "secret-read"),
        (ExecuteCommand(), "execute")
    ];

    public static bool IsLikelyRewrite(string proposedCommand, string untrustedText)
    {
        if (string.IsNullOrWhiteSpace(proposedCommand) || string.IsNullOrWhiteSpace(untrustedText)) return false;
        var proposedFamilies = Families.Where(item => item.Pattern.IsMatch(proposedCommand)).Select(item => item.Family).ToHashSet(StringComparer.Ordinal);
        if (proposedFamilies.Count == 0) return false;
        var proposedAnchors = ExtractAnchors(proposedCommand);
        if (proposedAnchors.Count == 0) return false;
        foreach (var line in untrustedText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Families.Any(item => proposedFamilies.Contains(item.Family) && item.Pattern.IsMatch(line))) continue;
            var lineAnchors = ExtractAnchors(line);
            var proposedUrls = Url().Matches(proposedCommand).Select(match => NormalizeAnchor(match.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lineUrls = Url().Matches(line).Select(match => NormalizeAnchor(match.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (proposedUrls.Count > 0 && lineUrls.Count > 0 && !proposedUrls.Overlaps(lineUrls)) continue;
            if (lineAnchors.Overlaps(proposedAnchors)) return true;
        }
        return false;
    }

    private static HashSet<string> ExtractAnchors(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Url().Matches(text)) Add(match.Value);
        foreach (Match match in AbsolutePath().Matches(text)) Add(match.Value);
        foreach (Match match in FileName().Matches(text)) Add(match.Value);
        foreach (Match match in SensitiveVariable().Matches(text)) Add(match.Value.TrimStart('$'));
        return result;

        void Add(string value)
        {
            var anchor = NormalizeAnchor(value);
            if (anchor.Length < 3) return;
            result.Add(anchor.Replace('\\', '/').ToLowerInvariant());
            var fileName = Path.GetFileName(anchor.Replace('\\', '/'));
            if (fileName.Length >= 4 && fileName.Contains('.', StringComparison.Ordinal)) result.Add(fileName.ToLowerInvariant());
        }
    }

    private static string NormalizeAnchor(string value) => value.Trim(' ', '\t', '\r', '\n', '"', '\'', '`', '(', ')', '[', ']', '{', '}', ',', ';', '.', ':');

    [GeneratedRegex(@"(?i)\b(?:rm|del|erase|rmdir|unlink|remove-item)\b")]
    private static partial Regex DeleteCommand();
    [GeneratedRegex(@"(?i)\b(?:curl|wget|invoke-webrequest|iwr|invoke-restmethod|irm|fetch)\b")]
    private static partial Regex NetworkCommand();
    [GeneratedRegex(@"(?i)\b(?:printenv|env|cat|type|get-content|gc)\b|\$env:")]
    private static partial Regex SecretReadCommand();
    [GeneratedRegex(@"(?i)\b(?:start-process|invoke-expression|iex|bash\s+-c|sh\s+-c|cmd\s+/c|powershell\s+-command)\b")]
    private static partial Regex ExecuteCommand();
    [GeneratedRegex(@"(?i)https?://[^\s\""'<>]+")]
    private static partial Regex Url();
    [GeneratedRegex(@"(?i)(?:[a-z]:[\\/][^\s\""'<>|;&]+|\.{0,2}[\\/][^\s\""'<>|;&]+)")]
    private static partial Regex AbsolutePath();
    [GeneratedRegex(@"\b[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{1,10}\b")]
    private static partial Regex FileName();
    [GeneratedRegex(@"\b[A-Z_][A-Z0-9_]{2,}\b")]
    private static partial Regex SensitiveVariable();
}

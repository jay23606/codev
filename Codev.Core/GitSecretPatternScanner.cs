using System.Text.RegularExpressions;

namespace Codev;

public sealed record GitSecretFinding(string FilePath, int Line, string Kind);

/// <summary>Finds common secret-shaped values on added lines without retaining or returning the value.</summary>
public static partial class GitSecretPatternScanner
{
    private const int MaxFindings = 100;

    [GeneratedRegex(@"(?i)(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})")]
    private static partial Regex GitHubToken();
    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();
    [GeneratedRegex(@"(?i)(?:sk-[A-Za-z0-9_-]{20,}|sk-ant-[A-Za-z0-9_-]{20,})")]
    private static partial Regex AiProviderKey();
    [GeneratedRegex(@"\bsk_live_[A-Za-z0-9]{20,}\b")]
    private static partial Regex StripeSecretKey();
    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleApiKey();
    [GeneratedRegex(@"\bnpm_[A-Za-z0-9]{36}\b")]
    private static partial Regex NpmToken();
    [GeneratedRegex(@"(?i)(?:xox[baprs]-[A-Za-z0-9-]{10,})")]
    private static partial Regex SlackToken();
    [GeneratedRegex(@"(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyHeader();
    [GeneratedRegex(@"(?i)\b(?:api[_-]?key|client[_-]?secret|password|passwd|token|secret)\b\s*[:=]\s*['\""']?([A-Za-z0-9_./+=:-]{12,})")]
    private static partial Regex GenericCredential();

    public static IReadOnlyList<GitSecretFinding> Scan(GitWorkingTreeReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var findings = new List<GitSecretFinding>();
        string? path = null;
        var lineNumber = 0;
        foreach (var line in (review.Diff ?? "").Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal)) { path = line[6..].TrimEnd('\r'); continue; }
            if (line.StartsWith("+++ ", StringComparison.Ordinal)) { path = null; continue; }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeader().Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var start)) lineNumber = start;
                continue;
            }
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                if (path is not null)
                {
                    var text = line[1..];
                    AddIfMatched(GitHubToken(), "GitHub token");
                    AddIfMatched(AwsAccessKey(), "AWS access key");
                    AddIfMatched(AiProviderKey(), "AI provider key");
                    AddIfMatched(StripeSecretKey(), "Stripe secret key");
                    AddIfMatched(GoogleApiKey(), "Google API key");
                    AddIfMatched(NpmToken(), "npm token");
                    AddIfMatched(SlackToken(), "Slack token");
                    AddIfMatched(PrivateKeyHeader(), "private key");
                    AddIfMatched(GenericCredential(), "credential-like assignment");
                    void AddIfMatched(Regex regex, string kind)
                    {
                        var match = regex.Match(text);
                        if (findings.Count < MaxFindings && match.Success && !(kind == "credential-like assignment" && IsPlaceholder(match.Groups[1].Value)) &&
                            !findings.Any(f => f.FilePath == path && f.Line == lineNumber && f.Kind == kind))
                            findings.Add(new GitSecretFinding(path, lineNumber, kind));
                    }
                }
                lineNumber++;
            }
            else if (line.StartsWith(' ') && !line.StartsWith("\\ ")) lineNumber++;
        }
        return findings;
    }

    private static bool IsPlaceholder(string value)
    {
        var normalized = value.Trim(' ', '\'', '"', '`', '<', '>').ToLowerInvariant();
        return normalized is "example" or "placeholder" or "changeme" or "change_me" or "dummy" or "test" or "none" or "null" or "redacted" ||
            normalized.StartsWith("your_", StringComparison.Ordinal) || normalized.StartsWith("your-", StringComparison.Ordinal) ||
            normalized.StartsWith("replace_", StringComparison.Ordinal) || normalized.StartsWith("replace-", StringComparison.Ordinal) ||
            normalized.StartsWith("example_", StringComparison.Ordinal) || normalized.StartsWith("example-", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
}

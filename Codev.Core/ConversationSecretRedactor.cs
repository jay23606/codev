using System.Text.RegularExpressions;

namespace Codev;

public sealed record SecretRedactionCandidate(string Value, string Location)
{
    public string DisplayValue => Value.Length <= 8 ? new string('•', Value.Length) : $"{Value[..4]}…{Value[^4..]}";
    public string Display => $"{DisplayValue} · {Location}";
}

/// <summary>Finds common credential-shaped strings for a user-reviewed export redaction pass.</summary>
public static partial class ConversationSecretRedactor
{
    private static readonly Regex KnownToken = new(
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CredentialAssignment = new(
        @"(?i)(?:api[_-]?key|access[_-]?token|auth(?:orization)?|password|passwd|secret)\s*[:=]\s*[\""']?([A-Za-z0-9_./+=-]{8,})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----[\s\S]{20,}?-----END (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<SecretRedactionCandidate> FindCandidates(
        Conversation conversation,
        IEnumerable<(string Text, string Location)>? additionalContent = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var locations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        Add(conversation.Title, "Conversation title");
        Add(conversation.Model, "Model name");
        if (!string.IsNullOrWhiteSpace(conversation.ProjectPath)) Add(Path.GetFileName(conversation.ProjectPath), "Project name");
        for (var index = 0; index < conversation.Messages.Count; index++)
            Add(conversation.Messages[index].Content, $"{(conversation.Messages[index].IsUser ? "You" : "Codev")} · message {index + 1}");
        foreach (var change in conversation.FileChanges)
            Add(change.RelativePath, $"Reviewed file path · {change.Kind}");
        if (additionalContent is not null)
            foreach (var (text, location) in additionalContent) Add(text, location);

        return locations.Select(pair => new SecretRedactionCandidate(pair.Key, string.Join(", ", pair.Value)))
            .OrderBy(candidate => candidate.Location, StringComparer.Ordinal)
            .Take(100)
            .ToArray();

        void Add(string? text, string location)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match match in KnownToken.Matches(text)) Record(match.Value, location);
            foreach (Match match in CredentialAssignment.Matches(text))
                if (match.Groups[1].Success) Record(match.Groups[1].Value, location);
            foreach (Match match in PrivateKeyBlock.Matches(text)) Record(match.Value, location);
        }

        void Record(string value, string location)
        {
            if (!locations.TryGetValue(value, out var foundAt)) locations[value] = foundAt = new HashSet<string>(StringComparer.Ordinal);
            foundAt.Add(location);
        }
    }

    public static string Redact(string? text, IEnumerable<string>? values)
    {
        if (string.IsNullOrEmpty(text) || values is null) return text ?? "";
        foreach (var value in values.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
            text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }
}

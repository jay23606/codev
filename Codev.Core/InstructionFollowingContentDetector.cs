using System.Text.RegularExpressions;

namespace Codev;

/// <summary>Flags text resembling common prompt-injection instructions for human review; it never blocks an action.</summary>
public static partial class InstructionFollowingContentDetector
{
    private static readonly (Regex Pattern, string Category)[] Patterns =
    [
        (OverrideInstructions(), "override or ignore prior instructions"),
        (ExposeSecrets(), "disclose or transmit secrets"),
        (BypassSafeguards(), "bypass approvals or security controls"),
        (ConcealFromUser(), "conceal actions from the user"),
        (ImpersonateAuthority(), "impersonate a privileged role")
    ];

    public static IReadOnlyList<string> Detect(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        return Patterns.Where(item => item.Pattern.IsMatch(content)).Select(item => item.Category).ToArray();
    }

    [GeneratedRegex(@"(?is)\b(ignore|disregard|override|forget)\b.{0,60}\b(previous|above|system|developer|user|instructions|rules|policy)\b")]
    private static partial Regex OverrideInstructions();
    [GeneratedRegex(@"(?is)\b(reveal|print|exfiltrate|send|upload|leak|share)\b.{0,60}\b(secret|token|api[_ -]?key|credential|password|environment variable|private key)\b")]
    private static partial Regex ExposeSecrets();
    [GeneratedRegex(@"(?is)\b(bypass|disable|turn off|skip|ignore)\b.{0,50}\b(approval|permission|security|safety|sandbox|validation|review)\b")]
    private static partial Regex BypassSafeguards();
    [GeneratedRegex(@"(?is)\b(do not|don't|never)\s+(tell|inform|show|alert)\s+(the\s+)?(user|owner|operator)\b")]
    private static partial Regex ConcealFromUser();
    [GeneratedRegex(@"(?is)\b(act as|pretend to be|you are now)\b.{0,60}\b(system|developer|admin|administrator|root|security officer)\b")]
    private static partial Regex ImpersonateAuthority();
}

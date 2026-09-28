namespace Codev;

/// <summary>Stable per-conversation response styles. These affect presentation only, never tool permissions.</summary>
public static class ConversationOutputStyles
{
    public const string Balanced = "balanced";
    public const string Concise = "concise";
    public const string Explanatory = "explanatory";
    public const string CodeOnly = "code-only";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Concise => Concise,
        Explanatory => Explanatory,
        CodeOnly => CodeOnly,
        _ => Balanced
    };

    public static string Instruction(string? value) => Normalize(value) switch
    {
        Concise => "Answer concisely and directly. Avoid repeating context and omit background that is not needed to answer the request. Preserve important caveats, actions taken, and verification results.",
        Explanatory => "Give a clear explanation of the approach, relevant assumptions, and non-obvious tradeoffs. Keep routine details brief and preserve important caveats and verification results.",
        CodeOnly => "Prefer the requested code, patch, or exact command with minimal surrounding prose. Add explanation only when needed for safe or correct use; always disclose unverified work, failures, and important caveats.",
        _ => "Use a balanced level of detail. Be clear and direct, with enough explanation to make the answer useful."
    };
}

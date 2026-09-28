namespace Codev;

/// <summary>Bounds and labels user-level writing/work preferences shared across all conversations.</summary>
public static class PersonalAgentInstructions
{
    public const int MaxCharacters = 12_000;

    public static string Normalize(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return "";
        var normalized = instructions.Trim();
        return normalized.Length > MaxCharacters ? normalized[..MaxCharacters].TrimEnd() : normalized;
    }

    public static string Build(string? instructions)
    {
        var normalized = Normalize(instructions);
        return normalized.Length == 0 ? "" :
            "Personal user preferences (apply when compatible with the current mode and safety requirements):\n" + normalized;
    }
}

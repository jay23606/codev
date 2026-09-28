namespace Codev;

/// <summary>Creates the mode-specific system instruction for read-only Avalonia conversations.</summary>
public static class ConversationSystemPrompt
{
    public static string Build(bool isPlanMode, bool isLocal)
    {
        var identity = isLocal
            ? "You are Codev, a practical coding assistant running locally."
            : "You are Codev, a practical coding assistant.";
        if (isPlanMode)
            return $"{identity} You are in read-only Plan mode. Analyze the request and available context, then give a concise ordered implementation plan. Identify likely files, important decisions, risks, and any questions that must be answered. Do not edit files, run commands, or claim to have done so. Ordinary chat is read-only.";
        return $"{identity} Be concise and focus on useful implementation details. Do not claim to have changed files or run commands. Ordinary chat is read-only; you have no tools to edit files or run commands.";
    }
}

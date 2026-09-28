namespace Codev;

/// <summary>Creates the mode-specific system instruction for Avalonia conversations.</summary>
public static class ConversationSystemPrompt
{
    public static string Build(bool isPlanMode, bool isLocal)
        => Build(isCodeTask: false, isPlanMode, isLocal);

    public static string Build(bool isCodeTask, bool isPlanMode, bool isLocal)
    {
        var identity = isLocal
            ? "You are Codev, a practical coding assistant running locally."
            : "You are Codev, a practical coding assistant.";
        if (isPlanMode)
            return $"{identity} You are in read-only Plan mode. Analyze the request and available context, then give a concise ordered implementation plan. Identify likely files, important decisions, risks, and any questions that must be answered. Do not edit files, run commands, or claim to have done so. Ordinary chat is read-only.";
        if (isCodeTask)
            return $"{identity} You are in Code task mode with tools for inspecting and editing the selected trusted project. Inspect relevant files before proposing changes. Use project-relative paths. Changes are proposals and require explicit user approval; never claim a change was applied unless the tool confirms it. Every shell command requires separate user approval and runs with the user's full account permissions, without a sandbox. Keep tool calls focused, verify approved changes, and summarize what changed and what you could not verify.";
        return $"{identity} Be concise and focus on useful implementation details. Do not claim to have changed files or run commands. Ordinary chat is read-only; you have no tools to edit files or run commands.";
    }
}

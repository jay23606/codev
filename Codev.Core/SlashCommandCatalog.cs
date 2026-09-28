namespace Codev;

public enum SlashCommandAction
{
    ClearConversation,
    ToggleCodeTask,
    ExportConversation,
    InitProject,
    SelectModel,
    TogglePlan,
    ReviewProject,
    ShowStatus
}

public sealed record SlashCommandDefinition(string Name, string Description, SlashCommandAction Action, string? Prompt = null);

/// <summary>Built-in composer commands that are currently backed by implemented Codev actions.</summary>
public static class SlashCommandCatalog
{
    private static readonly SlashCommandDefinition[] Commands =
    [
        new("/clear", "Clear this conversation's messages", SlashCommandAction.ClearConversation),
        new("/code", "Toggle trusted-project Code task mode", SlashCommandAction.ToggleCodeTask),
        new("/export", "Export this conversation as Markdown", SlashCommandAction.ExportConversation),
        new("/init", "Prepare a prompt to draft project guidance", SlashCommandAction.InitProject,
            "Inspect this project and draft a concise AGENTS.md with its purpose, layout, build and test commands, and important conventions. Do not overwrite or edit any file automatically. If Code task mode is available, propose AGENTS.md as a new file for my review; otherwise show the complete draft in your response."),
        new("/model", "Choose a different model", SlashCommandAction.SelectModel),
        new("/plan", "Toggle read-only Plan mode", SlashCommandAction.TogglePlan),
        new("/review", "Prepare a read-only project review prompt", SlashCommandAction.ReviewProject,
            "Review the available project context and recent changes for correctness bugs, security risks, edge cases, and missing tests. Do not edit files. Return only actionable findings, prioritized by severity, with file and line references where possible."),
        new("/status", "Show local provider, model, mode, context, and queue status", SlashCommandAction.ShowStatus)
    ];

    public static IReadOnlyList<SlashCommandDefinition> All => Commands;

    public static IReadOnlyList<SlashCommandDefinition> Suggest(string? draft, int? caretIndex = null)
    {
        if (string.IsNullOrEmpty(draft) || !draft.StartsWith('/') ||
            (caretIndex is not null && caretIndex != draft.Length) || draft.Any(char.IsWhiteSpace))
            return [];

        return Commands.Where(command => command.Name.StartsWith(draft, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static bool IsExactCommand(string? draft, SlashCommandDefinition command) =>
        string.Equals(draft?.Trim(), command.Name, StringComparison.OrdinalIgnoreCase);
}

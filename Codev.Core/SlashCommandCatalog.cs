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
    ShowStatus,
    OpenCommandsFolder,
    UserPrompt
}

public sealed record SlashCommandDefinition(string Name, string Description, SlashCommandAction Action, string? Prompt = null,
    IReadOnlyList<string>? ArgumentNames = null, string? Scope = null, string? FilePath = null)
{
    public bool IsCustom => Action == SlashCommandAction.UserPrompt;
    public string ScopeLabel => Scope switch { "project" => "PROJECT", "user" => "USER", _ => "BUILT IN" };
}

/// <summary>Built-in composer commands that are currently backed by implemented Codev actions.</summary>
public static class SlashCommandCatalog
{
    private static readonly SlashCommandDefinition[] Commands =
    [
        new("/clear", "Clear this conversation's messages", SlashCommandAction.ClearConversation),
        new("/code", "Toggle trusted-project Code task mode", SlashCommandAction.ToggleCodeTask),
        new("/commands", "Open the folders for user and trusted-project commands", SlashCommandAction.OpenCommandsFolder),
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

    public static bool TryGetCommandToken(string? draft, int? caretIndex, out string token, out bool hasArguments)
    {
        token = "";
        hasArguments = false;
        if (string.IsNullOrEmpty(draft) || !draft.StartsWith('/') ||
            (caretIndex is not null && caretIndex != draft.Length) || draft.Contains('\n') || draft.Contains('\r')) return false;
        var separator = draft.IndexOfAny([' ', '\t']);
        token = separator < 0 ? draft : draft[..separator];
        if (token.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) return false;
        hasArguments = separator >= 0;
        return true;
    }
}

using System.IO;

namespace Codev;

/// <summary>Loads optional root-level AGENTS.md guidance through the selected project's bounded file service.</summary>
public static class ProjectAgentInstructions
{
    public const int MaxCharacters = 20_000;
    public const string RelativePath = "AGENTS.md";

    public static async Task<string> LoadAsync(WorkspaceFileService service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var fullPath = service.ResolvePath(RelativePath);
        if (!File.Exists(fullPath) || service.IsContextExcluded(RelativePath)) return "";
        var content = (await service.ReadFileAsync(RelativePath, cancellationToken)).Trim();
        if (content.Length == 0) return "";
        var truncated = content.Length > MaxCharacters;
        if (truncated) content = content[..MaxCharacters].TrimEnd();
        return "Root AGENTS.md project guidance (user-provided; follow within the selected project where it does not conflict with higher-priority instructions):\n" + content +
               (truncated ? "\n[AGENTS.md was truncated at 20,000 characters.]" : "");
    }
}

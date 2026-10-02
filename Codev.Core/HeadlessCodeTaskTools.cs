using System.Text.Json;

namespace Codev;

/// <summary>Builds the headless Code task schema while keeping mutation and shell access opt-in.</summary>
public static class HeadlessCodeTaskTools
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "list_files", "read_file", "search_files"
    };

    public static object[] Create(ShellCommandSpec shell, bool useOllama, bool allowEdits = false, bool allowCommands = false)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var schemas = useOllama
            ? CodeTaskToolSchemaFactory.CreateOllamaTools(shell)
            : CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell);
        var allowed = new HashSet<string>(ReadOnlyTools, StringComparer.Ordinal);
        if (allowEdits) allowed.UnionWith(["create_file", "write_file", "apply_patch"]);
        if (allowCommands) allowed.UnionWith(["verify_command", "run_command"]);
        return schemas.Where(schema => TryGetName(schema, useOllama, out var name) && allowed.Contains(name)).ToArray();
    }

    private static bool TryGetName(object schema, bool useOllama, out string name)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(schema, JsonSerializerOptions.Web));
        var root = document.RootElement;
        var nameElement = useOllama
            ? root.GetProperty("function").GetProperty("name")
            : root.GetProperty("name");
        name = nameElement.GetString() ?? "";
        return name.Length > 0;
    }
}

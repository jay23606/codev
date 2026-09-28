using System.Text.Json;

namespace Codev;

/// <summary>Wraps project and process output in a stable data envelope before it enters a model prompt.</summary>
public static class UntrustedToolOutput
{
    public static string Format(string source, string content, string? path = null) =>
        JsonSerializer.Serialize(new ToolOutputEnvelope("untrusted_tool_output", source, path, content), JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ToolOutputEnvelope(string Type, string Source, string? Path, string Content);
}

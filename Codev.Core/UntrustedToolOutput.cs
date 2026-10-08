using System.Text.Json;

namespace Codev;

/// <summary>Wraps project and process output in a stable data envelope before it enters a model prompt.</summary>
public static class UntrustedToolOutput
{
    public static string Format(string source, string content, string? path = null, string? command = null, string? activity = null) =>
        JsonSerializer.Serialize(new ToolOutputEnvelope("untrusted_tool_output", source, path, content, command, activity), JsonOptions);

    public static string Truncate(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters) return value;
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "untrusted_tool_output" ||
                !root.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String)
                return value[..maxCharacters] + "\n… [tool output truncated]";
            var source = root.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() ?? "tool output" : "tool output";
            var path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
            var content = contentElement.GetString() ?? "";
            const string suffix = "\n… [tool output truncated]";
            var low = 0;
            var high = content.Length;
            var command = root.TryGetProperty("command", out var commandElement) ? commandElement.GetString() : null;
            var activity = root.TryGetProperty("activity", out var activityElement) ? activityElement.GetString() : null;
            var best = Format(source, suffix, path, command, activity);
            if (best.Length > maxCharacters) return value[..maxCharacters] + " [truncated]";
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = Format(source, content[..middle] + suffix, path, command, activity);
                if (candidate.Length <= maxCharacters) { best = candidate; low = middle + 1; }
                else high = middle - 1;
            }
            return best;
        }
        catch (JsonException) { return value[..maxCharacters] + "\n… [tool output truncated]"; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ToolOutputEnvelope(string Type, string Source, string? Path, string Content, string? Command = null, string? Activity = null);
}

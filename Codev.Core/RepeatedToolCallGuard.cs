using System.Text;
using System.Text.Json;

namespace Codev;

/// <summary>Counts consecutive identical tool requests so an agent can pause before looping.</summary>
public sealed class RepeatedToolCallGuard
{
    public const int ConfirmationThreshold = 3;

    private string? _lastSignature;
    private int _repeatCount;

    /// <returns>The number of consecutive times this exact tool request has appeared.</returns>
    public int Record(string toolName, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var signature = toolName + "\n" + Canonicalize(arguments);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            _repeatCount++;
        else
        {
            _lastSignature = signature;
            _repeatCount = 1;
        }
        return _repeatCount;
    }

    /// <summary>Clears the run after the user approves one more repeated call.</summary>
    public void AllowOneMore()
    {
        _lastSignature = null;
        _repeatCount = 0;
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(element, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

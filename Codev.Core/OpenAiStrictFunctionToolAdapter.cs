using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codev;

/// <summary>Converts Ollama-style function definitions to OpenAI Responses strict function tools.</summary>
public static class OpenAiStrictFunctionToolAdapter
{
    // These bounds remain enforced by Codev's tool-argument validator. Removing them from the schema
    // keeps strict function tools compatible with fine-tuned models, which support a smaller subset.
    private static readonly string[] RuntimeOnlyConstraints =
    ["minLength", "maxLength", "pattern", "format", "minimum", "maximum", "multipleOf", "minItems", "maxItems", "uniqueItems"];

    public static JsonObject Convert(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
            !function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
            !function.TryGetProperty("parameters", out var parameterElement) || parameterElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The function tool must have a name and object parameters.", nameof(tool));

        var parameters = JsonNode.Parse(parameterElement.GetRawText()) as JsonObject
            ?? throw new ArgumentException("The function tool parameters must be an object schema.", nameof(tool));
        NormalizeSchema(parameters);
        if (GetTypeName(parameters["type"]) != "object")
            throw new ArgumentException("The root function parameter schema must have type object.", nameof(tool));

        var result = new JsonObject
        {
            ["type"] = "function",
            ["name"] = name.GetString(),
            ["description"] = function.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                ? description.GetString() : "",
            ["parameters"] = parameters,
            ["strict"] = true
        };
        return result;
    }

    private static void NormalizeSchema(JsonObject schema)
    {
        foreach (var constraint in RuntimeOnlyConstraints) schema.Remove(constraint);

        if (GetTypeName(schema["type"]) == "object")
        {
            var properties = schema["properties"] as JsonObject ?? new JsonObject();
            var required = (schema["required"] as JsonArray ?? [])
                .Select(value => value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var name) ? name : null)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);
            schema["properties"] = properties;
            schema["required"] = new JsonArray(properties.Select(property => (JsonNode?)JsonValue.Create(property.Key)).ToArray());
            schema["additionalProperties"] = false;
            foreach (var property in properties.ToArray())
            {
                if (property.Value is not JsonObject nested) continue;
                NormalizeSchema(nested);
                if (!required.Contains(property.Key) && !AllowsNull(nested))
                {
                    properties[property.Key] = new JsonObject
                    {
                        ["anyOf"] = new JsonArray(property.Value!.DeepClone(), new JsonObject { ["type"] = "null" })
                    };
                }
            }
        }

        if (schema["items"] is JsonObject items) NormalizeSchema(items);
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (schema[keyword] is JsonArray alternatives)
            {
                foreach (var alternative in alternatives)
                    if (alternative is JsonObject nested) NormalizeSchema(nested);
            }
        }
    }

    private static bool AllowsNull(JsonObject schema)
    {
        if (schema["type"] is JsonArray types && types.Any(type =>
                type is JsonValue jsonType && jsonType.TryGetValue<string>(out var typeName) && typeName == "null")) return true;
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema[keyword] is JsonArray alternatives && alternatives.Any(alternative =>
                    alternative is JsonObject branch && AllowsNull(branch))) return true;
        return GetTypeName(schema["type"]) == "null";
    }

    private static string? GetTypeName(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var typeName) ? typeName : null;
}

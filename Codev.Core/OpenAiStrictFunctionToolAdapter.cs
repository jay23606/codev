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
        if (parameters["type"]?.GetValue<string>() != "object")
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

        if (schema["type"]?.GetValue<string>() == "object")
        {
            var properties = schema["properties"] as JsonObject ?? new JsonObject();
            schema["properties"] = properties;
            schema["required"] = new JsonArray(properties.Select(property => (JsonNode?)JsonValue.Create(property.Key)).ToArray());
            schema["additionalProperties"] = false;
            foreach (var property in properties.ToArray())
                if (property.Value is JsonObject nested) NormalizeSchema(nested);
        }

        if (schema["items"] is JsonObject items) NormalizeSchema(items);
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
            if (schema[keyword] is JsonArray alternatives)
                foreach (var alternative in alternatives)
                    if (alternative is JsonObject nested) NormalizeSchema(nested);
    }
}

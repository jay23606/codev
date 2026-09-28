using System.Text.Json;
using Codev;

namespace Codev.Tests;

public sealed class OpenAiStrictFunctionToolAdapterTests
{
    [Fact]
    public void Shared_code_task_tool_catalog_produces_valid_strict_schemas_for_every_openai_tool()
    {
        var shell = new ShellCommandSpec("powershell.exe", "PowerShell", []);
        var tools = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell)
            .Select(tool => JsonSerializer.SerializeToElement(tool)).ToArray();

        Assert.Equal(new[] { "list_files", "read_file", "search_files", "create_file", "write_file", "apply_patch", "verify_command", "update_task_checklist", "run_command" },
            tools.Select(tool => tool.GetProperty("name").GetString()));
        foreach (var tool in tools)
        {
            Assert.True(tool.GetProperty("strict").GetBoolean());
            AssertStrictSchema(tool.GetProperty("parameters"));
        }

        var checklist = tools.Single(tool => tool.GetProperty("name").GetString() == "update_task_checklist");
        Assert.Equal("items", checklist.GetProperty("parameters").GetProperty("required")[0].GetString());
    }

    [Fact]
    public void Converts_nested_function_schema_to_strict_form_and_leaves_size_checks_to_runtime()
    {
        using var source = JsonDocument.Parse("""
            {
              "type":"function",
              "function":{
                "name":"update_task_checklist",
                "description":"Update checklist",
                "parameters":{
                  "type":"object",
                  "properties":{
                    "items":{
                      "type":"array",
                      "maxItems":20,
                      "items":{
                        "type":"object",
                        "properties":{
                          "text":{"type":"string","minLength":1,"maxLength":240},
                          "status":{"type":"string","enum":["pending","in_progress","completed"]}
                        },
                        "required":["text","status"],
                        "additionalProperties":false
                      }
                    }
                  },
                  "required":["items"],
                  "additionalProperties":false
                }
              }
            }
            """);

        var tool = OpenAiStrictFunctionToolAdapter.Convert(source.RootElement);
        var parameters = tool["parameters"]!.AsObject();
        var itemSchema = parameters["properties"]!["items"]!.AsObject();
        var itemProperties = itemSchema["items"]!["properties"]!.AsObject();

        Assert.True(tool["strict"]!.GetValue<bool>());
        Assert.Equal("update_task_checklist", tool["name"]!.GetValue<string>());
        Assert.False((bool)parameters["additionalProperties"]!);
        Assert.Equal("items", Assert.Single(parameters["required"]!.AsArray())!.GetValue<string>());
        Assert.False((bool)itemSchema["items"]!["additionalProperties"]!);
        Assert.Equal(new[] { "text", "status" }, itemSchema["items"]!["required"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Null(itemSchema["maxItems"]);
        Assert.Null(itemProperties["text"]!["minLength"]);
        Assert.Null(itemProperties["text"]!["maxLength"]);
    }

    [Fact]
    public void Rejects_function_tools_without_an_object_parameter_schema()
    {
        using var source = JsonDocument.Parse("""{"type":"function","function":{"name":"bad","parameters":{"type":"array"}}}""");

        Assert.Throws<ArgumentException>(() => OpenAiStrictFunctionToolAdapter.Convert(source.RootElement));
    }

    private static void AssertStrictSchema(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
        {
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var names = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            var required = schema.GetProperty("required").EnumerateArray().Select(property => property.GetString()!).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(names, required);
            foreach (var property in schema.GetProperty("properties").EnumerateObject()) AssertStrictSchema(property.Value);
        }
        if (schema.TryGetProperty("items", out var items)) AssertStrictSchema(items);
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
            if (schema.TryGetProperty(keyword, out var alternatives))
                foreach (var alternative in alternatives.EnumerateArray()) AssertStrictSchema(alternative);
    }
}

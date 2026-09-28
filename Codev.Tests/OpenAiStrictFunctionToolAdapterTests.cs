using System.Text.Json;
using Codev;

namespace Codev.Tests;

public sealed class OpenAiStrictFunctionToolAdapterTests
{
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
}

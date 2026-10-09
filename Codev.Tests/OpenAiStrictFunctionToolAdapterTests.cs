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
    public void File_tool_descriptions_explain_that_auto_applies_without_review()
    {
        var shell = new ShellCommandSpec("powershell.exe", "PowerShell", []);
        var descriptions = CodeTaskToolSchemaFactory.CreateOllamaTools(shell)
            .Select(tool => JsonSerializer.SerializeToElement(tool))
            .Concat(CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell)
                .Select(tool => JsonSerializer.SerializeToElement(tool)))
            .Select(tool => tool.TryGetProperty("function", out var function) ? function : tool)
            .Where(tool => tool.GetProperty("name").GetString() is "create_file" or "write_file" or "apply_patch")
            .Select(tool => tool.GetProperty("description").GetString() ?? "")
            .ToArray();

        Assert.Equal(6, descriptions.Length);
        Assert.All(descriptions, description => Assert.Contains("Auto mode applies", description, StringComparison.Ordinal));
        Assert.All(descriptions, description => Assert.Contains("may show", description, StringComparison.Ordinal));
    }

    [Fact]
    public void Selected_profile_removes_denied_tools_from_strict_schema_but_keeps_approved_categories()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""");
        var mcpTool = new McpCodeTaskTool("mcp_github_search_1234567890abcdef", "github", "GitHub", "search", "Search issues.", schema.RootElement.Clone(), null!);
        var shell = new ShellCommandSpec("powershell.exe", "PowerShell", []);
        var askProfile = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Ask");
        var names = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, [mcpTool], askProfile)
            .Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString()).ToArray();

        Assert.Contains("read_file", names);
        Assert.Contains(mcpTool.FunctionName, names);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("create_file", names);
        Assert.DoesNotContain("run_command", names);
        Assert.DoesNotContain("verify_command", names);
    }

    [Fact]
    public void Plan_primary_agent_exposes_project_inspection_only()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""");
        var mcpTool = new McpCodeTaskTool("mcp_github_search_1234567890abcdef", "github", "GitHub", "search", "Search issues.", schema.RootElement.Clone(), null!);
        var shell = new ShellCommandSpec("powershell.exe", "PowerShell", []);
        var plan = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Plan");

        var names = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, [mcpTool], plan, allowDelegation: true)
            .Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString()).ToArray();

        Assert.Equal(new[] { "list_files", "read_file", "search_files" }, names);
    }

    [Fact]
    public void Imported_v2_glob_and_grep_rules_expose_strict_scoped_discovery_tools()
    {
        var shell = new ShellCommandSpec("powershell.exe", "PowerShell", []);
        var profile = new AgentProfile("Explore", "Explore source", null, null, null,
            AgentToolPermission.Ask, new Dictionary<string, AgentToolPermission>(), "", "user", "explore.md",
            OpenCodePermissionRules:
            [
                new("glob", "src/**/*.cs", AgentToolPermission.Allow),
                new("grep", "TODO.*", AgentToolPermission.Allow)
            ]);

        var tools = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, profile: profile)
            .Select(tool => JsonSerializer.SerializeToElement(tool)).ToArray();

        foreach (var name in new[] { "glob_files", "grep_files" })
        {
            var tool = Assert.Single(tools, candidate => candidate.GetProperty("name").GetString() == name);
            Assert.True(tool.GetProperty("strict").GetBoolean());
            Assert.Equal("pattern", tool.GetProperty("parameters").GetProperty("required")[0].GetString());
            AssertStrictSchema(tool.GetProperty("parameters"));
        }
    }

    [Fact]
    public void Delegation_schema_is_exposed_only_to_an_explicit_orchestrator_profile()
    {
        var shell = new ShellCommandSpec("powershell.exe", "PowerShell", []);
        var orchestrator = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Orchestrator");
        var code = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Code");

        var normal = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, profile: null)
            .Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString());
        var codeNames = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, profile: code, allowDelegation: true)
            .Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString());
        var orchestratorNames = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, profile: orchestrator, allowDelegation: true)
            .Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString());

        Assert.DoesNotContain("delegate_task", normal);
        Assert.DoesNotContain("delegate_task", codeNames);
        var delegation = JsonSerializer.SerializeToElement(CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(
                shell, profile: orchestrator, allowDelegation: true).Single(tool =>
                JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString() == "delegate_task"));
        Assert.Contains("delegate_task", orchestratorNames);
        Assert.True(delegation.GetProperty("strict").GetBoolean());
        AssertStrictSchema(delegation.GetProperty("parameters"));
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

    [Fact]
    public void Optional_tool_properties_become_required_nullable_fields_in_strict_schema()
    {
        using var source = JsonDocument.Parse("""
            {"type":"function","function":{"name":"mcp_optional","parameters":{"type":"object","properties":{"required":{"type":"string"},"optional":{"type":"string"},"already_nullable":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["required"],"additionalProperties":false}}}
            """);

        var result = OpenAiStrictFunctionToolAdapter.Convert(source.RootElement);
        var parameters = result["parameters"]!.AsObject();
        var properties = parameters["properties"]!.AsObject();

        Assert.Equal(new[] { "required", "optional", "already_nullable" },
            parameters["required"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal("string", properties["required"]!["type"]!.GetValue<string>());
        Assert.Equal(new[] { "string", "null" }, properties["optional"]!["anyOf"]!.AsArray()
            .Select(branch => branch!["type"]!.GetValue<string>()));
        Assert.Equal(new[] { "string", "null" }, properties["already_nullable"]!["anyOf"]!.AsArray()
            .Select(branch => branch!["type"]!.GetValue<string>()));
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

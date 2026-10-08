using System.Net;
using System.Text;
using System.Text.Json;
using Codev;

namespace Codev.Tests;

public sealed class OpenAiAutoMcpIntegrationTests
{
    [Fact]
    public async Task OpenAi_function_call_uses_auto_policy_and_a_reloaded_exact_deny_stops_the_server_call()
    {
        var project = Path.Combine(Path.GetTempPath(), "Codev-openai-auto-mcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        var permissionPath = Path.Combine(project, "mcp-permissions.json");
        var permissions = ProjectMcpToolPermissionRegistry.Load(permissionPath);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""");
        var tool = new McpCodeTaskTool("mcp_demo_search", "demo", "Demo server", "search", "Search the demo server.",
            schema.RootElement.Clone(), null, ConfigurationFingerprint: new string('a', 64));
        var calls = 0;
        var approvalPrompts = 0;
        var requests = new List<JsonDocument>();
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            requests.Add(JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return requests.Count % 2 == 1
                ? FunctionCallResponse(tool.FunctionName, $"call-{requests.Count}", "{\"query\":\"Codev\"}")
                : FinalTextResponse("The MCP request completed.");
        }));

        try
        {
            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(project), new Conversation(),
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                mcpTools: new Dictionary<string, McpCodeTaskTool>(StringComparer.Ordinal) { [tool.FunctionName] = tool },
                mcpPermissionApproval: (candidate, _, _) =>
                {
                    var decision = permissions.Evaluate(project, ProjectCommandPermissionMode.Auto, candidate.ServerId,
                        candidate.ToolName, candidate.PermissionFingerprint);
                    if (decision == ProjectCommandPermissionDecision.Ask) approvalPrompts++;
                    return Task.FromResult(decision switch
                    {
                        ProjectCommandPermissionDecision.Allow => CommandApprovalOutcome.Approved,
                        ProjectCommandPermissionDecision.Deny => CommandApprovalOutcome.Denied,
                        _ => CommandApprovalOutcome.Rejected
                    });
                },
                mcpCall: (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult("Found Codev results.");
                });
            var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));
            var tools = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(ShellCommandResolver.ResolveCurrent(), [tool]);

            var allowed = await runner.RunAsync("gpt-test", [new { role = "user", content = "Search using MCP." }], tools,
                (_, _, _) => Task.FromResult("test-key"),
                (name, arguments, token) => executor.ExecuteAsync(name, arguments, token),
                (_, _, _) => Task.FromResult(false));

            Assert.Equal(2, requests.Count);
            Assert.Equal(1, calls);
            Assert.Equal(0, approvalPrompts);
            Assert.Contains("Found Codev results.", allowed.Transcript, StringComparison.Ordinal);
            Assert.Contains("The MCP request completed.", allowed.Transcript, StringComparison.Ordinal);

            await permissions.SetRuleAsync(project, tool.ServerId, tool.ToolName, ProjectCommandPermissionDecision.Deny,
                tool.PermissionFingerprint);
            permissions = ProjectMcpToolPermissionRegistry.Load(permissionPath);

            var denied = await runner.RunAsync("gpt-test", [new { role = "user", content = "Search using MCP again." }], tools,
                (_, _, _) => Task.FromResult("test-key"),
                (name, arguments, token) => executor.ExecuteAsync(name, arguments, token),
                (_, _, _) => Task.FromResult(false));

            Assert.Equal(4, requests.Count);
            Assert.Equal(1, calls);
            Assert.Equal(0, approvalPrompts);
            Assert.Contains("Denied by a saved project MCP tool permission rule; the tool was not called.", denied.Transcript,
                StringComparison.Ordinal);
            var deniedFollowUpInput = requests[3].RootElement.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal("function_call_output", deniedFollowUpInput[2].GetProperty("type").GetString());
            var deniedToolOutput = deniedFollowUpInput[2].GetProperty("output").GetString();
            Assert.Contains("Denied by a saved project MCP tool permission rule; the tool was not called.", deniedToolOutput,
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch (IOException) { }
        }
    }

    private static HttpResponseMessage FunctionCallResponse(string name, string callId, string arguments)
    {
        var response = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { type = "function_call", call_id = callId, name, arguments } }
        });
        return EventResponse("response.completed", response);
    }

    private static HttpResponseMessage FinalTextResponse(string text)
    {
        var response = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } }
        });
        return EventResponse("response.completed", response);
    }

    private static HttpResponseMessage EventResponse(string eventType, string response) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"data: {{\"type\":\"{eventType}\",\"response\":{response}}}\n\n",
            Encoding.UTF8, "text/event-stream")
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}

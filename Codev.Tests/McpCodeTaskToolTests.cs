using Codev;
using System.Text.Json;
using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using System.Net;
using System.Net.Sockets;

namespace Codev.Tests;

public sealed class McpCodeTaskToolTests
{
    [Fact]
    public void Function_names_are_stable_and_namespaced_by_server_and_tool()
    {
        var first = McpCodeTaskSession.CreateFunctionName("github", "search");

        Assert.Equal(first, McpCodeTaskSession.CreateFunctionName("github", "search"));
        Assert.NotEqual(first, McpCodeTaskSession.CreateFunctionName("gitlab", "search"));
        Assert.NotEqual(first, McpCodeTaskSession.CreateFunctionName("github", "create_issue"));
        Assert.StartsWith("mcp_github_search_", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Ollama_schema_preserves_the_server_input_schema()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""");
        using var descriptor = JsonDocument.Parse("""{"type":"function","function":{"name":"search","parameters":{"type":"object"}}}""");
        var tool = new McpCodeTaskTool("mcp_github_search_abc", "github", "GitHub", "search", "Find repos.", schema.RootElement.Clone(), null!);
        var function = tool.ToOllamaFunctionTool();
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(function));

        Assert.Equal("mcp_github_search_abc", result.RootElement.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("query", result.RootElement.GetProperty("function").GetProperty("parameters").GetProperty("required")[0].GetString());
        Assert.Contains("untrusted", result.RootElement.GetProperty("function").GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Openai_tool_schema_is_strict_and_namespaced()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""");
        var tool = new McpCodeTaskTool("mcp_github_search_abc", "github", "GitHub", "search", "Find repos.", schema.RootElement.Clone(), null!);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(tool.ToOpenAiFunctionTool()));

        Assert.Equal("mcp_github_search_abc", result.RootElement.GetProperty("name").GetString());
        Assert.True(result.RootElement.GetProperty("strict").GetBoolean());
        Assert.False(result.RootElement.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Agent_profile_filters_mcp_tools_per_server_in_local_and_hosted_schemas()
    {
        const string profileText = "---\nname: GithubSearch\ndescription: Restrict MCP tools to repository search.\ntools: mcp_github_*=deny, mcp_github_search_*=allow\n---\nUse the GitHub search tool only.";
        Assert.True(AgentProfileCatalog.TryParse("github-search.md", profileText, "user", out var profile, out var error), error);
        var tools = new[]
        {
            CreateMcpTool("mcp_github_search_repos_0123456789abcdef", "github", "search_repos"),
            CreateMcpTool("mcp_github_create_issue_0123456789abcdef", "github", "create_issue"),
            CreateMcpTool("mcp_gitlab_search_projects_0123456789abcdef", "gitlab", "search_projects")
        };

        var localNames = ReadToolNames(CodeTaskToolSchemaFactory.CreateOllamaTools(ShellCommandResolver.ResolveCurrent(), tools, profile));
        var hostedNames = ReadToolNames(CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(ShellCommandResolver.ResolveCurrent(), tools, profile));

        Assert.Contains("mcp_github_search_repos_0123456789abcdef", localNames);
        Assert.DoesNotContain("mcp_github_create_issue_0123456789abcdef", localNames);
        Assert.Contains("mcp_gitlab_search_projects_0123456789abcdef", localNames);
        Assert.Contains("mcp_github_search_repos_0123456789abcdef", hostedNames);
        Assert.DoesNotContain("mcp_github_create_issue_0123456789abcdef", hostedNames);
        Assert.Contains("mcp_gitlab_search_projects_0123456789abcdef", hostedNames);
    }

    private static McpCodeTaskTool CreateMcpTool(string functionName, string serverId, string toolName)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
        return new McpCodeTaskTool(functionName, serverId, serverId, toolName, "Test MCP tool.", schema.RootElement.Clone(), null!);
    }

    private static HashSet<string> ReadToolNames(IEnumerable<object> schemas)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(schemas, JsonSerializerOptions.Web));
        return document.RootElement.EnumerateArray().Select(tool =>
            tool.TryGetProperty("function", out var function)
                ? function.GetProperty("name").GetString()!
                : tool.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Connection_diagnostics_are_bounded_collapsed_untrusted_transcript_entries()
    {
        await using var session = await McpCodeTaskSession.ConnectAsync([
            new McpServerConfiguration("demo", "Demo server", McpServerTransportKind.Stdio, Enabled: false, Command: "unused")
        ]);

        var transcript = session.ToConnectionTranscript();
        var parsed = ToolOutputTranscriptParser.Parse(transcript);

        Assert.Empty(parsed.DisplayText);
        var diagnostic = Assert.Single(parsed.Outputs);
        Assert.Equal("MCP connection · MCP connection diagnostics", diagnostic.Header);
        Assert.Contains("Demo server: disabled.", diagnostic.Content, StringComparison.Ordinal);
        Assert.Equal("mcp_connection", diagnostic.Activity);
    }

    [Fact]
    public async Task In_memory_mcp_server_initializes_lists_tools_and_returns_a_real_tool_result()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var toolCalls = 0;
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ToolCollection =
                [
                    McpServerTool.Create((string message) =>
                    {
                        Interlocked.Increment(ref toolCalls);
                        return $"Echo: {message}";
                    }, new() { Name = "echo" })
                ]
            });
        var serverRun = server.RunAsync();
        var workspace = Path.Combine(Path.GetTempPath(), "Codev-mcp-live-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            await using var session = await McpCodeTaskSession.ConnectWithTransportFactoryAsync(
                [new McpServerConfiguration("memory", "In-memory test", McpServerTransportKind.Stdio, Enabled: true, Command: "unused")],
                _ => new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: CancellationToken.None);
            var tool = Assert.Single(session.Tools.Values);
            using var arguments = JsonDocument.Parse("""{"message":"hello"}""");

            Assert.Equal("echo", tool.ToolName);
            Assert.Contains("connected; 1 tool(s) available", Assert.Single(session.ConnectionLog), StringComparison.Ordinal);
            var permissions = ProjectMcpToolPermissionRegistry.Load(Path.Combine(workspace, "permissions.json"));
            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(workspace), new Conversation(),
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                mcpTools: session.Tools,
                mcpPermissionApproval: (mcpTool, _, _) => Task.FromResult(
                    permissions.Evaluate(workspace, ProjectCommandPermissionMode.Auto, mcpTool.ServerId, mcpTool.ToolName) switch
                    {
                        ProjectCommandPermissionDecision.Allow => CommandApprovalOutcome.Approved,
                        ProjectCommandPermissionDecision.Deny => CommandApprovalOutcome.Denied,
                        _ => CommandApprovalOutcome.Rejected
                    }));

            var result = await executor.ExecuteAsync(tool.FunctionName, arguments.RootElement);
            var parsedResult = ToolOutputTranscriptParser.Parse("**MCP tool result**\n" + result);
            Assert.True(parsedResult.Outputs.Count == 1, result);
            var returned = parsedResult.Outputs[0];
            Assert.Equal("mcp_tool", returned.Activity);
            Assert.Contains("Echo: hello", returned.Content, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref toolCalls));

            await permissions.SetRuleAsync(workspace, tool.ServerId, tool.ToolName, ProjectCommandPermissionDecision.Deny);
            var denied = await executor.ExecuteAsync(tool.FunctionName, arguments.RootElement);
            Assert.Contains("Denied by a saved project MCP tool permission rule", denied, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
        }
        finally
        {
            await server.DisposeAsync();
            await serverRun.WaitAsync(TimeSpan.FromSeconds(3));
            await clientToServer.Writer.CompleteAsync();
            await clientToServer.Reader.CompleteAsync();
            await serverToClient.Writer.CompleteAsync();
            await serverToClient.Reader.CompleteAsync();
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Http_auto_detect_falls_back_to_a_legacy_sse_server_and_calls_its_tool()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = false;
#pragma warning disable MCP9004
                options.EnableLegacySse = true;
#pragma warning restore MCP9004
            })
            .WithTools<LegacySseTestTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            await using var session = await McpCodeTaskSession.ConnectAsync([
                new McpServerConfiguration("legacy-sse", "Legacy SSE test", McpServerTransportKind.Http, Enabled: true,
                    Url: $"http://127.0.0.1:{port}/mcp")
            ]);

            var tool = Assert.Single(session.Tools.Values);
            using var arguments = JsonDocument.Parse("""{"message":"hello"}""");
            var result = await session.CallAsync(tool.FunctionName, arguments.RootElement);

            Assert.Equal("echo", tool.ToolName);
            Assert.Contains("Echo from legacy SSE: hello", result, StringComparison.Ordinal);
            Assert.Contains("connected; 1 tool(s) available", Assert.Single(session.ConnectionLog), StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
    [McpServerToolType]
    private sealed class LegacySseTestTools
    {
        [McpServerTool]
        public static string Echo(string message) => "Echo from legacy SSE: " + message;
    }

    [Fact]
    public async Task Duplicate_server_id_isolated_without_skipping_unrelated_servers()
    {
        await using var session = await McpCodeTaskSession.ConnectAsync([
            new McpServerConfiguration("shared", "First", McpServerTransportKind.Stdio, Enabled: false, Command: "unused"),
            new McpServerConfiguration("SHARED", "Duplicate", McpServerTransportKind.Stdio, Enabled: false, Command: "unused"),
            new McpServerConfiguration("other", "Other", McpServerTransportKind.Stdio, Enabled: false, Command: "unused")
        ]);

        Assert.Contains("First: disabled.", session.ConnectionLog);
        Assert.Contains("Duplicate: skipped; another configured server already uses ID 'SHARED'.", session.ConnectionLog);
        Assert.Contains("Other: disabled.", session.ConnectionLog);
    }

    [Fact]
    public async Task Mcp_phase_timeout_returns_promptly_and_cancels_the_underlying_operation()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => McpOperationTimeout.RunAsync(async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, 30));

        Assert.IsType<TimeoutException>(error);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Mcp_operation_timeout_preserves_user_cancellation_as_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = McpOperationTimeout.RunAsync(async token =>
        {
            operationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, 5_000, cancellation.Token);

        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public void Mcp_output_formatter_bounds_combined_blocks_and_marks_truncation()
    {
        var output = new System.Text.StringBuilder();
        var truncated = false;
        McpToolOutputFormatter.Append(output, new string('x', 7_990), ref truncated);
        McpToolOutputFormatter.Append(output, new string('y', 500_000), ref truncated);
        McpToolOutputFormatter.Append(output, "must not append", ref truncated);

        Assert.True(truncated);
        Assert.Equal(McpToolOutputFormatter.MaxCharacters, output.Length);
        Assert.Contains("MCP tool output truncated", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("must not append", output.ToString(), StringComparison.Ordinal);
    }
}

using System.Net.Http.Json;
using System.IO.Pipelines;
using System.Text.Json;
using Codev;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codev.Tests;

/// <summary>Opt-in live coverage for native Ollama tool calling through Codev's Auto command policy.</summary>
public sealed class OllamaAutoPolicyIntegrationTests
{
    [LocalOllamaFact]
    public async Task Live_model_mcp_call_runs_through_auto_and_exact_deny_without_an_approval_prompt()
    {
        var endpointText = Environment.GetEnvironmentVariable("CODEV_OLLAMA_URL") ?? "http://127.0.0.1:11434";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback ||
            endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The live Ollama test accepts only a loopback HTTP(S) endpoint.");

        var model = Environment.GetEnvironmentVariable("CODEV_OLLAMA_MODEL") ?? "qwen3.8:27b";
        const string expectedMessage = "Codev MCP integration smoke";
        var root = Path.Combine(Path.GetTempPath(), "Codev-live-ollama-mcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
        _ = server.RunAsync();

        try
        {
            await using var session = await McpCodeTaskSession.ConnectWithTransportFactoryAsync(
                [new McpServerConfiguration("smoke", "MCP live smoke", McpServerTransportKind.Stdio,
                    Enabled: true, Command: "unused")],
                _ => new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: CancellationToken.None);
            var mcpTool = Assert.Single(session.Tools.Values,
                tool => tool.Operation == McpCodeTaskOperationKind.Tool);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var shell = ShellCommandResolver.ResolveCurrent();
            var tools = CodeTaskToolSchemaFactory.CreateOllamaTools(shell, session.Tools.Values);
            using var response = await http.PostAsJsonAsync(new Uri(endpoint, "/api/chat"), new
            {
                model,
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = "Use only the named MCP tool. Do not invent tool output or answer without calling it." },
                    new { role = "user", content = $"Call `{mcpTool.FunctionName}` exactly once with message `{expectedMessage}`. Do not call another tool." }
                },
                tools,
                think = false,
                options = new { temperature = 0, num_predict = 500 }
            });
            response.EnsureSuccessStatusCode();
            using var modelResult = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var message = modelResult.RootElement.GetProperty("message");
            Assert.True(message.TryGetProperty("tool_calls", out var callArray) && callArray.ValueKind == JsonValueKind.Array,
                $"Model {model} returned no tool call. Message: {message.GetRawText()}");
            var call = Assert.Single(callArray.EnumerateArray());
            var function = call.GetProperty("function");
            Assert.Equal(mcpTool.FunctionName, function.GetProperty("name").GetString());
            var arguments = function.GetProperty("arguments").Clone();
            Assert.Equal(expectedMessage, arguments.GetProperty("message").GetString());

            var mcpPermissions = ProjectMcpToolPermissionRegistry.Load(Path.Combine(root, "mcp-permissions.json"));
            var commandPermissions = ProjectCommandPermissionRegistry.Load(Path.Combine(root, "command-permissions.json"));
            await commandPermissions.SetModeAsync(root, ProjectCommandPermissionMode.Auto);
            var approvalPromptShown = false;
            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(root), new Conversation(),
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                mcpTools: session.Tools,
                mcpCall: (tool, args, token) => session.CallAsync(tool.FunctionName, args, token),
                mcpPermissionApproval: (tool, _, _) =>
                {
                    var decision = mcpPermissions.Evaluate(root, commandPermissions.GetMode(root), tool.ServerId, tool.ToolName);
                    if (decision == ProjectCommandPermissionDecision.Ask) approvalPromptShown = true;
                    return Task.FromResult(decision switch
                    {
                        ProjectCommandPermissionDecision.Allow => CommandApprovalOutcome.Approved,
                        ProjectCommandPermissionDecision.Deny => CommandApprovalOutcome.Denied,
                        _ => CommandApprovalOutcome.Rejected
                    });
                });

            var result = await executor.ExecuteAsync(mcpTool.FunctionName, arguments);
            Assert.Contains("Echo: " + expectedMessage, result, StringComparison.Ordinal);
            Assert.Contains("untrusted_tool_output", result, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
            Assert.False(approvalPromptShown);

            await mcpPermissions.SetRuleAsync(root, mcpTool.ServerId, mcpTool.ToolName, ProjectCommandPermissionDecision.Deny);
            var denied = await executor.ExecuteAsync(mcpTool.FunctionName, arguments);
            Assert.Contains("Denied by a saved project MCP tool permission rule", denied, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
            Assert.False(approvalPromptShown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [LocalOllamaFact]
    public async Task Live_model_verification_call_runs_through_auto_without_requesting_approval()
    {
        var endpointText = Environment.GetEnvironmentVariable("CODEV_OLLAMA_URL") ?? "http://127.0.0.1:11434";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback ||
            endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The live Ollama test accepts only a loopback HTTP(S) endpoint.");

        var model = Environment.GetEnvironmentVariable("CODEV_OLLAMA_MODEL") ?? "qwen3.8:27b";
        const string expectedCommand = "dotnet --version";
        var root = Path.Combine(Path.GetTempPath(), "Codev-live-ollama-auto", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var shell = ShellCommandResolver.ResolveCurrent();
            var tools = CodeTaskToolSchemaFactory.CreateOllamaTools(shell);
            using var response = await http.PostAsJsonAsync(new Uri(endpoint, "/api/chat"), new
            {
                model,
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = "Use the requested Codev tool. Do not run tools yourself or invent command results." },
                    new { role = "user", content = $"Call verify_command exactly once with command `{expectedCommand}`. Do not use another tool or command." }
                },
                tools,
                think = false,
                options = new { temperature = 0, num_predict = 500 }
            });
            response.EnsureSuccessStatusCode();
            using var modelResult = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var message = modelResult.RootElement.GetProperty("message");
            Assert.True(message.TryGetProperty("tool_calls", out var callArray) && callArray.ValueKind == JsonValueKind.Array,
                $"Model {model} returned no tool call. Message: {message.GetRawText()}");
            var calls = callArray.EnumerateArray().ToArray();
            var call = Assert.Single(calls);
            var function = call.GetProperty("function");
            Assert.Equal("verify_command", function.GetProperty("name").GetString());
            var arguments = function.GetProperty("arguments").Clone();
            Assert.Equal(expectedCommand, arguments.GetProperty("command").GetString());

            var conversation = new Conversation();
            var permissions = ProjectCommandPermissionRegistry.Load(Path.Combine(root, "permissions.json"));
            await permissions.SetModeAsync(root, ProjectCommandPermissionMode.Auto);
            var policy = new ProjectCommandApprovalPolicy(permissions);
            var approvalPromptShown = false;
            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(root), conversation,
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                permissionApproval: async proposal => (await policy.ApproveAsync(proposal,
                    requestApproval: _ =>
                    {
                        approvalPromptShown = true;
                        return Task.FromResult(ProjectCommandApprovalChoice.Cancel);
                    })).Outcome);

            var result = await executor.ExecuteAsync("verify_command", arguments);

            Assert.Contains("Verification PASSED (exit code 0)", result, StringComparison.Ordinal);
            Assert.False(approvalPromptShown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class LocalOllamaFactAttribute : FactAttribute
{
    public LocalOllamaFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEV_OLLAMA_LIVE_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set CODEV_OLLAMA_LIVE_TESTS=1 to run the local Ollama integration test.";
    }
}

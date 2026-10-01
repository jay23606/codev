using System.Net.Http.Json;
using System.Text.Json;
using Codev;

namespace Codev.Tests;

/// <summary>Opt-in live coverage for native Ollama tool calling through Codev's Auto command policy.</summary>
public sealed class OllamaAutoPolicyIntegrationTests
{
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

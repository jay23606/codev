using Codev;
using System.Text.Json;
using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Authentication;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

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

    [Fact]
    public void Custom_profile_ask_for_mcp_tools_defers_to_auto_but_asks_in_ask_mode()
    {
        const string profileText = "---\nname: ReviewMcp\ndescription: Ask before external MCP operations.\ntools: mcp_*=ask\n---\nInspect before calling MCP tools.";
        Assert.True(AgentProfileCatalog.TryParse("review-mcp.md", profileText, "user", out var profile, out var error), error);
        var permission = AgentProfilePolicy.PermissionFor(profile, "mcp_github_search_0123456789abcdef");

        Assert.Equal(AgentToolPermission.Ask, permission);
        Assert.False(AgentProfilePolicy.RequiresOneCallApproval(permission, ProjectCommandPermissionMode.Auto));
        Assert.True(AgentProfilePolicy.RequiresOneCallApproval(permission, ProjectCommandPermissionMode.AskEveryTime));
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
        var resourceReads = 0;
        var resourceTemplateReads = 0;
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
                ],
                PromptCollection =
                [
                    McpServerPrompt.Create((string subject) => $"Review {subject} for correctness.",
                        new() { Name = "review_subject", Description = "Review one subject." })
                ],
                ResourceCollection =
                [
                    McpServerResource.Create(() =>
                    {
                        Interlocked.Increment(ref resourceReads);
                        return "Shared MCP notes.";
                    },
                        new() { UriTemplate = "test://mcp/notes", Name = "notes", Description = "Shared notes.", MimeType = "text/plain" }),
                    McpServerResource.Create((string id) =>
                    {
                        Interlocked.Increment(ref resourceTemplateReads);
                        return $"Item {id}.";
                    }, new() { UriTemplate = "test://mcp/items/{id}", Name = "item_by_id", Description = "Read one item.", MimeType = "text/plain" })
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
            var tool = Assert.Single(session.Tools.Values, candidate => candidate.Operation == McpCodeTaskOperationKind.Tool);
            using var arguments = JsonDocument.Parse("""{"message":"hello"}""");

            Assert.Equal("echo", tool.ToolName);
            Assert.Equal(4, session.Tools.Count);
            using var ollamaSchemas = JsonDocument.Parse(JsonSerializer.Serialize(
                CodeTaskToolSchemaFactory.CreateOllamaTools(ShellCommandResolver.ResolveCurrent(), session.Tools.Values), JsonSerializerOptions.Web));
            var ollamaNames = ollamaSchemas.RootElement.EnumerateArray()
                .Select(schema => schema.GetProperty("function").GetProperty("name").GetString()).ToArray();
            using var openAiSchemas = JsonDocument.Parse(JsonSerializer.Serialize(
                CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(ShellCommandResolver.ResolveCurrent(), session.Tools.Values), JsonSerializerOptions.Web));
            var openAiNames = openAiSchemas.RootElement.EnumerateArray().Select(schema => schema.GetProperty("name").GetString()).ToArray();
            Assert.Contains("connected; 1 tool(s), 1 prompt(s), 1 resource(s), and 1 resource template(s) available", Assert.Single(session.ConnectionLog), StringComparison.Ordinal);
            var prompt = Assert.Single(session.Prompts.Values);
            Assert.Equal("review_subject", prompt.Name);
            var promptText = await session.GetPromptAsync(prompt.FunctionName, new Dictionary<string, string> { ["subject"] = "the parser" });
            Assert.Contains("Review the parser for correctness.", promptText, StringComparison.Ordinal);
            Assert.Contains("untrusted_tool_output", promptText, StringComparison.Ordinal);
            var resource = Assert.Single(session.Resources.Values);
            Assert.Equal("notes", resource.Name);
            var resourceTemplate = Assert.Single(session.ResourceTemplates.Values);
            Assert.Equal("item_by_id", resourceTemplate.Name);
            var resourceText = await session.ReadResourceAsync(resource.FunctionName);
            Assert.Contains("Shared MCP notes.", resourceText, StringComparison.Ordinal);
            Assert.Contains("untrusted_tool_output", resourceText, StringComparison.Ordinal);
            using var promptArguments = JsonDocument.Parse("""{"subject":"the parser"}""");
            var promptTool = session.Tools[prompt.FunctionName];
            Assert.Equal(McpCodeTaskOperationKind.Prompt, promptTool.Operation);
            Assert.Contains("Review the parser for correctness.", await session.CallAsync(prompt.FunctionName, promptArguments.RootElement));
            Assert.Contains(prompt.FunctionName, ollamaNames);
            Assert.Contains(resource.FunctionName, ollamaNames);
            Assert.Contains(resourceTemplate.FunctionName, ollamaNames);
            Assert.Contains(prompt.FunctionName, openAiNames);
            Assert.Contains(resource.FunctionName, openAiNames);
            Assert.Contains(resourceTemplate.FunctionName, openAiNames);
            using var templateArguments = JsonDocument.Parse("""{"id":"42"}""");
            Assert.Contains("Item 42.", await session.CallAsync(resourceTemplate.FunctionName, templateArguments.RootElement));
            Assert.Equal(1, Volatile.Read(ref resourceTemplateReads));
            var resourceTool = session.Tools[resource.FunctionName];
            Assert.Equal(McpCodeTaskOperationKind.Resource, resourceTool.Operation);
            Assert.Contains("Shared MCP notes.", await session.CallAsync(resource.FunctionName, JsonDocument.Parse("{}").RootElement));
            var permissions = ProjectMcpToolPermissionRegistry.Load(Path.Combine(workspace, "permissions.json"));
            var commandPermissions = ProjectCommandPermissionRegistry.Load(Path.Combine(workspace, "command-permissions.json"));
            await commandPermissions.SetModeAsync(workspace, ProjectCommandPermissionMode.Auto);
            const string profileText = "---\nname: ReviewMcp\ndescription: Ask before external MCP operations.\ntools: mcp_*=ask\n---\nInspect before calling MCP tools.";
            Assert.True(AgentProfileCatalog.TryParse("review-mcp.md", profileText, "user", out var profile, out var profileError), profileError);
            var promptCount = 0;
            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(workspace), new Conversation(),
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                mcpTools: session.Tools,
                mcpCall: (mcpTool, args, token) => session.CallAsync(mcpTool.FunctionName, args, token),
                agentProfile: profile,
                agentProfilePermission: (name, _) =>
                {
                    var permission = AgentProfilePolicy.PermissionFor(profile, name);
                    return Task.FromResult(permission == AgentToolPermission.Deny
                        ? AgentToolProfileDecision.Denied
                        : AgentProfilePolicy.RequiresOneCallApproval(permission, commandPermissions.GetMode(workspace))
                            ? AgentToolProfileDecision.Rejected
                            : AgentToolProfileDecision.DeferToProjectPolicy);
                },
                mcpPermissionApproval: (mcpTool, _, _) =>
                {
                    var decision = permissions.Evaluate(workspace, commandPermissions.GetMode(workspace), mcpTool.ServerId, mcpTool.ToolName);
                    if (decision == ProjectCommandPermissionDecision.Ask) promptCount++;
                    return Task.FromResult(decision switch
                    {
                        ProjectCommandPermissionDecision.Allow => CommandApprovalOutcome.Approved,
                        ProjectCommandPermissionDecision.Deny => CommandApprovalOutcome.Denied,
                        _ => CommandApprovalOutcome.Rejected
                    });
                });

            var result = await executor.ExecuteAsync(tool.FunctionName, arguments.RootElement);
            var parsedResult = ToolOutputTranscriptParser.Parse("**MCP tool result**\n" + result);
            Assert.True(parsedResult.Outputs.Count == 1, result);
            var returned = parsedResult.Outputs[0];
            Assert.Equal("mcp_tool", returned.Activity);
            Assert.Contains("Echo: hello", returned.Content, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
            Assert.Equal(0, promptCount);
            var promptResult = await executor.ExecuteAsync(prompt.FunctionName, promptArguments.RootElement);
            Assert.Contains("Review the parser for correctness.", promptResult, StringComparison.Ordinal);
            Assert.Contains("untrusted_tool_output", promptResult, StringComparison.Ordinal);
            using var emptyArguments = JsonDocument.Parse("{}");
            var resourceResult = await executor.ExecuteAsync(resource.FunctionName, emptyArguments.RootElement);
            Assert.Contains("Shared MCP notes.", resourceResult, StringComparison.Ordinal);
            Assert.Contains("untrusted_tool_output", resourceResult, StringComparison.Ordinal);
            Assert.Equal(0, promptCount);
            Assert.Equal(3, Volatile.Read(ref resourceReads));
            var templateResult = await executor.ExecuteAsync(resourceTemplate.FunctionName, templateArguments.RootElement);
            Assert.Contains("Item 42.", templateResult, StringComparison.Ordinal);
            Assert.Contains("untrusted_tool_output", templateResult, StringComparison.Ordinal);
            var parsedTemplate = ToolOutputTranscriptParser.Parse("**MCP resource template output**\n" + templateResult);
            Assert.Equal("mcp_resource_template", Assert.Single(parsedTemplate.Outputs).Activity);
            Assert.Contains("read MCP resource templates", ToolOutputSummary.Build(parsedTemplate.Outputs), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, Volatile.Read(ref resourceTemplateReads));

            await permissions.SetRuleAsync(workspace, tool.ServerId, tool.ToolName, ProjectCommandPermissionDecision.Deny);
            var denied = await executor.ExecuteAsync(tool.FunctionName, arguments.RootElement);
            Assert.Contains("Denied by a saved project MCP tool permission rule", denied, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
            await permissions.SetRuleAsync(workspace, resource.ServerId, "resource " + resource.Name, ProjectCommandPermissionDecision.Deny);
            var deniedResource = await executor.ExecuteAsync(resource.FunctionName, emptyArguments.RootElement);
            Assert.Contains("Denied by a saved project MCP tool permission rule", deniedResource, StringComparison.Ordinal);
            Assert.Equal(3, Volatile.Read(ref resourceReads));
            await permissions.SetRuleAsync(workspace, resourceTemplate.ServerId, "resource template " + resourceTemplate.Name, ProjectCommandPermissionDecision.Deny);
            var deniedTemplate = await executor.ExecuteAsync(resourceTemplate.FunctionName, templateArguments.RootElement);
            Assert.Contains("Denied by a saved project MCP tool permission rule", deniedTemplate, StringComparison.Ordinal);
            Assert.Equal(2, Volatile.Read(ref resourceTemplateReads));
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

            var tool = Assert.Single(session.Tools.Values, candidate => candidate.Operation == McpCodeTaskOperationKind.Tool);
            using var arguments = JsonDocument.Parse("""{"message":"hello"}""");
            var result = await session.CallAsync(tool.FunctionName, arguments.RootElement);

            Assert.Equal("echo", tool.ToolName);
            Assert.Contains("Echo from legacy SSE: hello", result, StringComparison.Ordinal);
            Assert.Contains("connected; 1 tool(s), 0 prompt(s), 0 resource(s), and 0 resource template(s) available", Assert.Single(session.ConnectionLog), StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Http_mcp_custom_secret_header_is_not_forwarded_across_redirects()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var targetPortReservation = new TcpListener(IPAddress.Loopback, 0);
        targetPortReservation.Start();
        var targetPort = ((IPEndPoint)targetPortReservation.LocalEndpoint).Port;
        targetPortReservation.Stop();

        const string environmentVariable = "CODEV_TEST_MCP_REDIRECT_SECRET";
        const string secret = "test-mcp-secret-never-forward";
        var previousSecret = Environment.GetEnvironmentVariable(environmentVariable);
        var sourceSawSecret = false;
        var targetRequestCount = 0;
        var targetUri = $"http://127.0.0.1:{targetPort}/target";
        Environment.SetEnvironmentVariable(environmentVariable, secret);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Run(context =>
        {
            sourceSawSecret = context.Request.Headers["X-Mcp-Test-Key"] == secret;
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = targetUri;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var targetBuilder = WebApplication.CreateBuilder();
        targetBuilder.Logging.ClearProviders();
        targetBuilder.WebHost.UseUrls($"http://127.0.0.1:{targetPort}");
        var targetApp = targetBuilder.Build();
        targetApp.Run(context =>
        {
            Interlocked.Increment(ref targetRequestCount);
            return Task.CompletedTask;
        });
        await targetApp.StartAsync();

        try
        {
            await using var session = await McpCodeTaskSession.ConnectAsync([
                new McpServerConfiguration("redirect", "Redirect test", McpServerTransportKind.Http, Enabled: true,
                    Url: $"http://127.0.0.1:{port}/source", HeaderEnvironmentVariables: new Dictionary<string, string>
                    {
                        ["X-Mcp-Test-Key"] = environmentVariable
                    }, OAuthEnabled: false)
            ]);

            Assert.True(sourceSawSecret, "The configured secret header should reach the configured MCP endpoint.");
            Assert.Equal(0, Volatile.Read(ref targetRequestCount));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            await targetApp.StopAsync();
            await targetApp.DisposeAsync();
            Environment.SetEnvironmentVariable(environmentVariable, previousSecret);
        }
    }

    [McpServerToolType]
    private sealed class LegacySseTestTools
    {
        [McpServerTool]
        public static string Echo(string message) => "Echo from legacy SSE: " + message;
    }

    [Fact]
    public async Task OAuth_http_mcp_server_signs_in_persists_tokens_and_refreshes_them_without_reauthorization()
    {
        var useNativeCredentialStore = string.Equals(Environment.GetEnvironmentVariable("CODEV_TEST_NATIVE_MCP_OAUTH_VAULT"), "1", StringComparison.Ordinal);
        if (useNativeCredentialStore && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new InvalidOperationException("The opt-in native MCP credential-vault test supports Windows Credential Manager, macOS Keychain, and Linux Secret Service.");
        CloudApiKeyVault? nativeVault = useNativeCredentialStore ? new CloudApiKeyVault() : null;
        IMcpOAuthTokenVault vault = (IMcpOAuthTokenVault?)nativeVault ?? new MemoryMcpOAuthTokenVault();
        string? vaultAccount = null;
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var origin = new Uri($"http://127.0.0.1:{port}/");
        var issuer = new Uri(origin, "/").AbsoluteUri.TrimEnd('/');
        var mcpUri = new Uri(origin, "mcp");
        var resourceMetadataUri = new Uri(origin, ".well-known/oauth-protected-resource/mcp");
        var tokenEndpointUri = new Uri(origin, "token");
        var authorizedRequests = 0;
        var authorizationCodeExchanges = 0;
        var refreshExchanges = 0;
        var seenCodeVerifier = "";
        var browserLaunches = 0;
        var browserCallbackStatus = 0;
        var authorizeCodeChallenge = "";
        Task<int>? browserCallbackTask = null;
        var acceptedTokens = new HashSet<string>(StringComparer.Ordinal) { "access-token-1", "access-token-2" };
        var requestEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var statusEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var browserEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(origin.AbsoluteUri.TrimEnd('/'));
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = false)
            .WithTools<OAuthIntegrationTools>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            requestEvents.Enqueue($"{context.Request.Method} {context.Request.Path}");
            if (context.Request.Path == "/mcp")
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                var accessToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authorization[7..] : "";
                if (!acceptedTokens.Contains(accessToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceMetadataUri.AbsoluteUri}\", scope=\"mcp\"";
                    return;
                }
                Interlocked.Increment(ref authorizedRequests);
            }
            await next();
        });
        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => { requestEvents.Enqueue("PRM metadata"); return Results.Json(new
        {
            resource = mcpUri.AbsoluteUri,
            authorization_servers = new[] { issuer },
            scopes_supported = new[] { "mcp" }
        }); });
        app.MapGet("/.well-known/oauth-authorization-server", () => { requestEvents.Enqueue("authorization metadata"); return Results.Json(new
        {
            issuer,
            authorization_endpoint = new Uri(origin, "authorize").AbsoluteUri,
            token_endpoint = tokenEndpointUri.AbsoluteUri,
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            code_challenge_methods_supported = new[] { "S256" },
            authorization_response_iss_parameter_supported = true
        }); });
        app.MapGet("/authorize", () => Results.Ok("The test browser launcher completes the redirect."));
        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            requestEvents.Enqueue("token " + form["grant_type"]);
            if (form["grant_type"] == "authorization_code")
            {
                Interlocked.Increment(ref authorizationCodeExchanges);
                seenCodeVerifier = form["code_verifier"].ToString();
                return Results.Json(new
                {
                    access_token = "access-token-1",
                    token_type = "Bearer",
                    expires_in = 3_600,
                    refresh_token = "refresh-token",
                    scope = "mcp"
                });
            }
            if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "refresh-token")
            {
                Interlocked.Increment(ref refreshExchanges);
                return Results.Json(new
                {
                    access_token = "access-token-2",
                    token_type = "Bearer",
                    expires_in = 3_600,
                    refresh_token = "refresh-token",
                    scope = "mcp"
                });
            }
            return Results.BadRequest();
        });
        app.MapMcp("/mcp");
        await app.StartAsync();

        var configuration = new McpServerConfiguration("oauth-test", "OAuth test", McpServerTransportKind.Http,
            Enabled: true, Url: mcpUri.AbsoluteUri, OAuthEnabled: true, OAuthClientId: "codev-oauth-test-client", OAuthScopes: ["mcp"]);
        vaultAccount = McpOAuthTokenCache.CreateAccount(configuration);
        if (nativeVault is not null) await nativeVault.RemoveTokensAsync(vaultAccount);
        var openBrowser = (Uri authorizationUri, CancellationToken cancellationToken) =>
        {
            Interlocked.Increment(ref browserLaunches);
            browserEvents.Enqueue(authorizationUri.AbsoluteUri);
            var query = System.Web.HttpUtility.ParseQueryString(authorizationUri.Query);
            authorizeCodeChallenge = query["code_challenge"] ?? "";
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("codev-oauth-test-client", query["client_id"]);
            Assert.Equal("mcp", query["scope"]);
            var redirectUri = new Uri(query["redirect_uri"] ?? throw new InvalidDataException("OAuth redirect URI was not supplied."));
            var callback = new Uri(redirectUri.AbsoluteUri + "?code=authorization-code&state=" +
                Uri.EscapeDataString(query["state"] ?? "") + "&iss=" + Uri.EscapeDataString(issuer));
            browserCallbackTask = CompleteBrowserCallbackAsync(callback, cancellationToken);
            return Task.CompletedTask;
        };

        try
        {
            McpCodeTaskSession session;
            try
            {
                session = await McpCodeTaskSession.ConnectAsync([configuration], status: message => statusEvents.Enqueue(message), oauthTokenVault: vault,
                    oauthBrowserOpener: openBrowser).WaitAsync(TimeSpan.FromMinutes(1));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"OAuth startup did not complete. HTTP events: {string.Join(" | ", requestEvents)}; status: {string.Join(" | ", statusEvents)}; browser launches: {browserLaunches}; browser events: {string.Join(" | ", browserEvents)}.", ex);
            }
            await using (session)
            {
                var tool = Assert.Single(session.Tools.Values, candidate => candidate.Operation == McpCodeTaskOperationKind.Tool);
                using var arguments = JsonDocument.Parse("""{"message":"first"}""");
                Assert.Contains("OAuth echo: first", await session.CallAsync(tool.FunctionName, arguments.RootElement));
            }

            var account = vaultAccount!;
            var savedText = await vault.GetTokensAsync(account);
            Assert.NotNull(savedText);
            var saved = JsonSerializer.Deserialize<TokenContainer>(savedText!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal("access-token-1", saved?.AccessToken);
            Assert.Equal("refresh-token", saved?.RefreshToken);
            Assert.NotEmpty(seenCodeVerifier);
            Assert.Equal(authorizeCodeChallenge, ToPkceChallenge(seenCodeVerifier));
            Assert.Equal(1, Volatile.Read(ref browserLaunches));
            Assert.Equal(1, Volatile.Read(ref authorizationCodeExchanges));
            Assert.NotNull(browserCallbackTask);
            browserCallbackStatus = await browserCallbackTask;
            browserEvents.Enqueue("callback status=" + browserCallbackStatus);
            Assert.Equal((int)HttpStatusCode.OK, browserCallbackStatus);

            saved!.ExpiresIn = 1;
            saved.ObtainedAt = DateTimeOffset.UtcNow.AddHours(-1);
            await vault.SaveTokensAsync(account, JsonSerializer.Serialize(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await using (var restartedSession = await McpCodeTaskSession.ConnectAsync([configuration], oauthTokenVault: vault,
                             oauthBrowserOpener: openBrowser).WaitAsync(TimeSpan.FromMinutes(1)))
            {
                var tool = Assert.Single(restartedSession.Tools.Values);
                using var arguments = JsonDocument.Parse("""{"message":"refreshed"}""");
                Assert.Contains("OAuth echo: refreshed", await restartedSession.CallAsync(tool.FunctionName, arguments.RootElement));
            }

            Assert.Equal(1, Volatile.Read(ref browserLaunches));
            Assert.Equal(1, Volatile.Read(ref refreshExchanges));
            Assert.True(Volatile.Read(ref authorizedRequests) >= 4);
        }
        finally
        {
            try
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            finally
            {
                if (nativeVault is not null && vaultAccount is not null)
                    await nativeVault.RemoveTokensAsync(vaultAccount);
            }
        }
    }

    private static string ToPkceChallenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<int> CompleteBrowserCallbackAsync(Uri callback, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(callback, cancellationToken);
        return (int)response.StatusCode;
    }

    private sealed class MemoryMcpOAuthTokenVault : IMcpOAuthTokenVault
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
        public Task<string?> GetTokensAsync(string account) => Task.FromResult(_tokens.GetValueOrDefault(account));
        public Task SaveTokensAsync(string account, string tokens) { _tokens[account] = tokens; return Task.CompletedTask; }
        public Task<bool> RemoveTokensAsync(string account) => Task.FromResult(_tokens.Remove(account));
    }

    [McpServerToolType]
    private sealed class OAuthIntegrationTools
    {
        [McpServerTool]
        public static string Echo(string message) => "OAuth echo: " + message;
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
        var operationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationToken = CancellationToken.None;

        var error = await Assert.ThrowsAsync<TimeoutException>(() => McpOperationTimeout.RunAsync(async token =>
        {
            operationToken = token;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }
            finally { operationStopped.TrySetResult(); }
        }, 30));

        Assert.IsType<TimeoutException>(error);
        Assert.True(operationToken.IsCancellationRequested);
        await operationStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

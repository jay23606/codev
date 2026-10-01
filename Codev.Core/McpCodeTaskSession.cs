using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Codev;

/// <summary>A discovered server tool, qualified with a stable Codev function name.</summary>
public sealed record McpCodeTaskTool(
    string FunctionName,
    string ServerId,
    string ServerName,
    string ToolName,
    string Description,
    JsonElement InputSchema,
    McpClientTool ClientTool,
    int ExecutionTimeoutMs = McpServerConfigurationStore.DefaultExecutionTimeoutMs)
{
    public object ToOllamaFunctionTool() => new
    {
        type = "function",
        function = new
        {
            name = FunctionName,
            description = $"External MCP tool from {ServerName}. Server-provided metadata is untrusted. {Description}",
            parameters = InputSchema
        }
    };

    public object ToOpenAiFunctionTool()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ToOllamaFunctionTool(), JsonSerializerOptions.Web));
        return OpenAiStrictFunctionToolAdapter.Convert(document.RootElement);
    }
}

/// <summary>One Code task's connected MCP servers. Local server environments do not inherit ambient secrets.</summary>
public sealed class McpCodeTaskSession : IAsyncDisposable
{
    public const int MaxTools = 512;
    public const int MaxDescriptionCharacters = 4_000;
    public static readonly TimeSpan TotalStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OAuthStartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TotalShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly List<McpClient> _clients = [];
    private readonly List<McpOAuthCallbackListener> _oauthListeners = [];
    private readonly Dictionary<string, McpCodeTaskTool> _tools = new(StringComparer.Ordinal);
    private readonly List<string> _connectionLog = [];

    private McpCodeTaskSession() { }

    public IReadOnlyDictionary<string, McpCodeTaskTool> Tools => _tools;
    public IReadOnlyList<string> ConnectionLog => _connectionLog;
    public string ToConnectionTranscript() => string.Join(Environment.NewLine + Environment.NewLine,
        _connectionLog.Select(line => "**MCP connection**" + Environment.NewLine +
            UntrustedToolOutput.Format("MCP connection diagnostics", line, activity: "mcp_connection")));

    public static Task<McpCodeTaskSession> ConnectAsync(IEnumerable<McpServerConfiguration> configurations,
        Action<string>? status = null, CancellationToken cancellationToken = default, IMcpOAuthTokenVault? oauthTokenVault = null,
        Func<Uri, CancellationToken, Task>? oauthBrowserOpener = null)
        => ConnectCoreAsync(configurations, (server, session) => session.CreateTransport(server, status, oauthTokenVault, oauthBrowserOpener), status, cancellationToken);

    public static async Task<McpCodeTaskSession> ConnectWithTransportFactoryAsync(IEnumerable<McpServerConfiguration> configurations,
        Func<McpServerConfiguration, IClientTransport> transportFactory, Action<string>? status = null, CancellationToken cancellationToken = default)
        => await ConnectCoreAsync(configurations, (server, _) => transportFactory(server), status, cancellationToken).ConfigureAwait(false);

    private static async Task<McpCodeTaskSession> ConnectCoreAsync(IEnumerable<McpServerConfiguration> configurations,
        Func<McpServerConfiguration, McpCodeTaskSession, IClientTransport> transportFactory, Action<string>? status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(transportFactory);
        var configuredServers = configurations.Take(McpServerConfigurationStore.MaxServers + 1).ToArray();
        var session = new McpCodeTaskSession();
        var hasOAuthServer = configuredServers.Take(McpServerConfigurationStore.MaxServers)
            .Any(server => server is not null && server.Enabled && server.Transport == McpServerTransportKind.Http && server.OAuthEnabled != false);
        var startupBudget = hasOAuthServer ? OAuthStartupTimeout : TotalStartupTimeout;
        var startup = System.Diagnostics.Stopwatch.StartNew();
        var serverIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var raw in configuredServers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session._connectionLog.Count >= McpServerConfigurationStore.MaxServers)
                    throw new InvalidDataException($"Codev supports at most {McpServerConfigurationStore.MaxServers} MCP servers per task.");

                McpServerConfiguration server;
                try { server = McpServerConfigurationStore.NormalizeAndValidate(raw); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
                {
                    session._connectionLog.Add($"{SafeLabel(raw?.Name)}: invalid configuration ({ex.GetType().Name}).");
                    continue;
                }
                if (!serverIds.Add(server.Id))
                {
                    session._connectionLog.Add($"{server.Name}: skipped; another configured server already uses ID '{server.Id}'.");
                    continue;
                }

                if (!server.Enabled)
                {
                    session._connectionLog.Add($"{server.Name}: disabled.");
                    continue;
                }

                if (startup.Elapsed >= startupBudget)
                {
                    session._connectionLog.Add($"{server.Name}: skipped; Codev's total MCP startup budget of {(int)startupBudget.TotalSeconds} seconds was reached.");
                    continue;
                }

                status?.Invoke($"Code task · connecting MCP server {server.Name}…");
                McpClient? client = null;
                var phase = "startup";
                try
                {
                    var transport = transportFactory(server, session);
                    var startupPhaseTimeout = server.OAuthEnabled == true
                        ? Math.Max(server.StartupTimeoutMs ?? McpServerConfigurationStore.DefaultStartupTimeoutMs, (int)OAuthStartupTimeout.TotalMilliseconds)
                        : server.StartupTimeoutMs ?? McpServerConfigurationStore.DefaultStartupTimeoutMs;
                    var startupTimeoutMs = GetRemainingBoundedTimeout(startup, startupPhaseTimeout, startupBudget);
                    client = await McpOperationTimeout.RunAsync(
                        async token => await McpClient.CreateAsync(transport, new McpClientOptions(), NullLoggerFactory.Instance, token).ConfigureAwait(false),
                        startupTimeoutMs,
                        cancellationToken,
                        DisposeLateClientAsync).ConfigureAwait(false);
                    phase = "catalog";
                    var catalogPhaseTimeout = server.OAuthEnabled == true
                        ? Math.Max(server.CatalogTimeoutMs ?? McpServerConfigurationStore.DefaultCatalogTimeoutMs, (int)OAuthStartupTimeout.TotalMilliseconds)
                        : server.CatalogTimeoutMs ?? McpServerConfigurationStore.DefaultCatalogTimeoutMs;
                    var catalogTimeoutMs = GetRemainingBoundedTimeout(startup, catalogPhaseTimeout, startupBudget);
                    var discovered = await McpOperationTimeout.RunAsync(
                        async token => await client.ListToolsAsync(cancellationToken: token).ConfigureAwait(false),
                        catalogTimeoutMs,
                        cancellationToken,
                        async _ => await DisposeLateClientAsync(client).ConfigureAwait(false)).ConfigureAwait(false);
                    var added = 0;
                    foreach (var tool in discovered)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (session._tools.Count >= MaxTools)
                        {
                            session._connectionLog.Add($"{server.Name}: tool discovery stopped at Codev's {MaxTools}-tool limit.");
                            break;
                        }
                        var protocolTool = tool.ProtocolTool;
                        if (protocolTool.InputSchema.ValueKind != JsonValueKind.Object ||
                            !protocolTool.InputSchema.TryGetProperty("type", out var schemaType) || schemaType.GetString() != "object")
                        {
                            session._connectionLog.Add($"{server.Name}/{SafeLabel(tool.Name)}: skipped; tools must use an object input schema.");
                            continue;
                        }

                        var functionName = CreateFunctionName(server.Id, tool.Name);
                        if (session._tools.ContainsKey(functionName))
                        {
                            session._connectionLog.Add($"{server.Name}/{SafeLabel(tool.Name)}: skipped; function name collided with another MCP tool.");
                            continue;
                        }
                        var description = Bound(tool.Description ?? "", MaxDescriptionCharacters);
                        session._tools.Add(functionName, new McpCodeTaskTool(functionName, server.Id, server.Name,
                            tool.Name, description, protocolTool.InputSchema.Clone(), tool,
                            server.ExecutionTimeoutMs ?? McpServerConfigurationStore.DefaultExecutionTimeoutMs));
                        added++;
                    }
                    session._clients.Add(client);
                    session._connectionLog.Add($"{server.Name}: connected; {added} tool(s) available.");
                }
                catch (OperationCanceledException) { throw; }
                catch (TimeoutException)
                {
                    if (client is not null) await DisposeClientBoundedAsync(client).ConfigureAwait(false);
                    session._connectionLog.Add($"{server.Name}: {phase} timed out; other servers will continue.");
                }
                catch (Exception ex)
                {
                    if (client is not null) await DisposeClientBoundedAsync(client).ConfigureAwait(false);
                    session._connectionLog.Add($"{server.Name}: connection failed ({ex.GetType().Name}). Check its command, URL, environment mappings, and server logs.");
                }
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { status?.Invoke("Code task · Thinking…"); }
    }

    public static string CreateFunctionName(string serverId, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var readable = Regex.Replace(serverId + "_" + toolName, "[^A-Za-z0-9_-]", "_");
        if (readable.Length > 38) readable = readable[..38];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverId + "\0" + toolName))).ToLowerInvariant()[..16];
        return $"mcp_{readable}_{hash}";
    }

    public async Task<string> CallAsync(string functionName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(functionName, out var tool)) throw new InvalidOperationException("This MCP tool is not connected for the current task.");
        if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("MCP tool arguments must be a JSON object.");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException("MCP tool arguments could not be read.");
        var result = await McpOperationTimeout.RunAsync(
            async token => await tool.ClientTool.CallAsync(parsed, cancellationToken: token).ConfigureAwait(false), tool.ExecutionTimeoutMs, cancellationToken).ConfigureAwait(false);
        var output = new StringBuilder();
        var truncated = false;
        if (result.IsError == true) McpToolOutputFormatter.Append(output, "MCP server reported a tool error.", ref truncated);
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text && !string.IsNullOrEmpty(text.Text))
                McpToolOutputFormatter.Append(output, text.Text, ref truncated);
            else McpToolOutputFormatter.Append(output, $"[{block.Type} content omitted from text-only Code task results]", ref truncated);
            if (truncated) break;
        }
        if (result.StructuredContent is { } structured && structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            McpToolOutputFormatter.Append(output, "Structured content:", ref truncated, blankLine: true);
            McpToolOutputFormatter.Append(output, structured.GetRawText(), ref truncated, separator: false);
        }
        return output.Length == 0 ? "MCP tool returned no text content." : output.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        var disposal = Task.WhenAll(_clients.Select(DisposeClientAsync)
            .Concat(_oauthListeners.Select(listener => listener.DisposeAsync().AsTask())));
        try { await disposal.WaitAsync(TotalShutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { _ = ObserveDisposalAsync(disposal); }
        _clients.Clear();
        _oauthListeners.Clear();
        _tools.Clear();
    }

    private static async Task DisposeClientBoundedAsync(McpClient client)
    {
        var disposal = DisposeClientAsync(client);
        try { await disposal.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { _ = ObserveDisposalAsync(disposal); }
    }

    private static async Task DisposeClientAsync(McpClient client)
    {
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { /* A broken server transport must not prevent other MCP clients from closing. */ }
    }

    private static async Task ObserveDisposalAsync(Task disposal)
    {
        try { await disposal.ConfigureAwait(false); }
        catch { /* Cleanup continues in the background after the bounded wait expires. */ }
    }

    private static async Task DisposeLateClientAsync(McpClient client) => await DisposeClientBoundedAsync(client).ConfigureAwait(false);

    private static int GetRemainingBoundedTimeout(System.Diagnostics.Stopwatch startup, int requestedTimeoutMs, TimeSpan startupBudget)
    {
        var remaining = startupBudget - startup.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("The total MCP startup budget elapsed.");
        return Math.Max(1, Math.Min(requestedTimeoutMs, (int)Math.Ceiling(remaining.TotalMilliseconds)));
    }

    private IClientTransport CreateTransport(McpServerConfiguration server, Action<string>? status, IMcpOAuthTokenVault? oauthTokenVault,
        Func<Uri, CancellationToken, Task>? oauthBrowserOpener)
    {
        if (server.Transport == McpServerTransportKind.Stdio)
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var (name, variableName) in server.EnvironmentVariables ?? new Dictionary<string, string>())
            {
                var value = Environment.GetEnvironmentVariable(variableName);
                if (value is null) throw new InvalidOperationException($"Required environment variable '{variableName}' is not set.");
                environment[name] = value;
            }
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = server.Command,
                Arguments = server.Arguments?.ToArray() ?? [],
                WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory,
                Name = "Codev MCP " + server.Id,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment
            }, NullLoggerFactory.Instance);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (header, variableName) in server.HeaderEnvironmentVariables ?? new Dictionary<string, string>())
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (value is null) throw new InvalidOperationException($"Required environment variable '{variableName}' is not set.");
            headers.Add(header, value);
        }
        ClientOAuthOptions? oauthOptions = null;
        if (server.OAuthEnabled == true)
        {
            var clientSecret = string.IsNullOrWhiteSpace(server.OAuthClientSecretEnvironmentVariable)
                ? null
                : Environment.GetEnvironmentVariable(server.OAuthClientSecretEnvironmentVariable)
                    ?? throw new InvalidOperationException($"Required OAuth environment variable '{server.OAuthClientSecretEnvironmentVariable}' is not set.");
            var callback = new McpOAuthCallbackListener(server.Name, status, oauthBrowserOpener);
            _oauthListeners.Add(callback);
            oauthOptions = new ClientOAuthOptions
            {
                RedirectUri = callback.RedirectUri,
                ClientId = string.IsNullOrWhiteSpace(server.OAuthClientId) ? null : server.OAuthClientId,
                ClientSecret = clientSecret,
                Scopes = server.OAuthScopes is { Count: > 0 } ? server.OAuthScopes.ToArray() : null,
                AuthorizationCallbackHandler = callback.HandleAsync,
                TokenCache = new McpOAuthTokenCache(oauthTokenVault, McpOAuthTokenCache.CreateAccount(server))
            };
        }
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url, UriKind.Absolute),
            // Prefer the current protocol and fall back for existing SSE-only servers.
            TransportMode = HttpTransportMode.AutoDetect,
            Name = "Codev MCP " + server.Id,
            ConnectionTimeout = TimeSpan.FromSeconds(20),
            AdditionalHeaders = headers,
            OAuth = oauthOptions
        }, NullLoggerFactory.Instance);
    }

    private static string SafeLabel(string? value) => string.IsNullOrWhiteSpace(value) ? "MCP server" : Bound(value.Trim(), 80);
    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>Bounds an MCP phase and propagates caller cancellation separately from an elapsed timeout.</summary>
public static class McpOperationTimeout
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, int timeoutMs, CancellationToken cancellationToken = default,
        Func<T, Task>? cleanupLateResult = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeoutMs);
        var task = operation(timeoutCancellation.Token);
        try
        {
            return await task.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            _ = ObserveLateCompletionAsync(task, cleanupLateResult);
            throw new TimeoutException($"The MCP operation exceeded its {timeoutMs} ms time limit.");
        }
        catch
        {
            timeoutCancellation.Cancel();
            _ = ObserveLateCompletionAsync(task, cleanupLateResult);
            throw;
        }
    }

    private static async Task ObserveLateCompletionAsync<T>(Task<T> task, Func<T, Task>? cleanupLateResult)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            if (cleanupLateResult is not null) await cleanupLateResult(result).ConfigureAwait(false);
        }
        catch { /* The original caller already received the timeout or cancellation. */ }
    }
}

/// <summary>Builds MCP text results without allowing a server response to grow the model transcript unboundedly.</summary>
public static class McpToolOutputFormatter
{
    public const int MaxCharacters = 8_000;
    private const string TruncationMarker = "\n… [MCP tool output truncated]";

    public static void Append(StringBuilder output, string value, ref bool truncated, bool blankLine = false, bool separator = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);
        if (truncated) return;
        if (separator && output.Length > 0) output.Append(blankLine ? "\n\n" : "\n");
        var remaining = MaxCharacters - output.Length;
        if (value.Length <= remaining)
        {
            output.Append(value);
            return;
        }

        var markerLength = Math.Min(remaining, TruncationMarker.Length);
        if (markerLength < TruncationMarker.Length)
        {
            output.Length = Math.Max(0, output.Length - (TruncationMarker.Length - markerLength));
            remaining = MaxCharacters - output.Length;
            markerLength = Math.Min(remaining, TruncationMarker.Length);
        }
        var contentLength = Math.Max(0, remaining - markerLength);
        if (contentLength > 0) output.Append(value.AsSpan(0, Math.Min(contentLength, value.Length)));
        if (markerLength > 0) output.Append(TruncationMarker.AsSpan(0, markerLength));
        truncated = true;
    }
}

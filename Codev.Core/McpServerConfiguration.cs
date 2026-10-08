using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Codev;

public enum McpServerTransportKind
{
    Stdio,
    Http
}

/// <summary>
/// User-level MCP server configuration. Credential values are deliberately not stored here:
/// stdio environment values and HTTP headers refer to names in the current process environment.
/// </summary>
public sealed record McpServerConfiguration(
    string Id,
    string Name,
    McpServerTransportKind Transport,
    bool Enabled,
    string Command = "",
    IReadOnlyList<string>? Arguments = null,
    string WorkingDirectory = "",
    string Url = "",
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    IReadOnlyDictionary<string, string>? HeaderEnvironmentVariables = null,
    int? StartupTimeoutMs = null,
    int? CatalogTimeoutMs = null,
    int? ExecutionTimeoutMs = null,
    bool? OAuthEnabled = null,
    string OAuthClientId = "",
    string OAuthClientSecretEnvironmentVariable = "",
    IReadOnlyList<string>? OAuthScopes = null);

/// <summary>Loads and stores bounded MCP server configuration outside project folders.</summary>
public sealed class McpServerConfigurationStore(string path)
{
    public const int MaxServers = 32;
    public const int MaxFileBytes = 1024 * 1024;
    public const int MinTimeoutMs = 1_000;
    public const int MaxStartupOrCatalogTimeoutMs = 120_000;
    public const int MaxExecutionTimeoutMs = 600_000;
    public const int DefaultStartupTimeoutMs = 30_000;
    public const int DefaultCatalogTimeoutMs = 30_000;
    public const int DefaultExecutionTimeoutMs = 120_000;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HttpHeaderNamePattern = new("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<McpServerConfiguration>? _servers;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    public async Task<IReadOnlyList<McpServerConfiguration>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_servers is not null) return _servers.Select(Clone).ToArray();
            if (!File.Exists(_path)) return _servers = [];
            if (new FileInfo(_path).Length > MaxFileBytes) throw new InvalidDataException("The MCP server configuration exceeds the size limit.");
            var text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The MCP server configuration must be a JSON array.");
            if (document.RootElement.GetArrayLength() > MaxServers)
                throw new InvalidDataException($"Codev supports at most {MaxServers} MCP servers.");
            var loaded = new List<McpServerConfiguration>(document.RootElement.GetArrayLength());
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    loaded.Add(JsonSerializer.Deserialize<McpServerConfiguration>(element.GetRawText(), JsonOptions)
                        ?? InvalidEntry(index));
                }
                catch (JsonException)
                {
                    loaded.Add(InvalidEntry(index));
                }
                index++;
            }
            _servers = loaded.Select(Clone).ToArray();
            return _servers.Select(Clone).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IEnumerable<McpServerConfiguration> servers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var normalized = servers.Select(NormalizeAndValidate).Select(Clone).ToArray();
        ValidateCollection(normalized);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new InvalidDataException("The MCP server configuration exceeds the size limit.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicTextFile.WriteAsync(_path, json, cancellationToken).ConfigureAwait(false);
            _servers = normalized.Select(Clone).ToArray();
        }
        finally { _gate.Release(); }
    }

    public static McpServerConfiguration NormalizeAndValidate(McpServerConfiguration server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var id = (server.Id ?? string.Empty).Trim();
        var name = (server.Name ?? string.Empty).Trim();
        if (!IdPattern.IsMatch(id)) throw new ArgumentException("MCP server IDs must use 1–40 letters, numbers, underscores, or hyphens.", nameof(server));
        if (name.Length is 0 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("MCP server names must contain 1–80 printable characters.", nameof(server));
        if (!Enum.IsDefined(server.Transport)) throw new ArgumentException("The MCP transport is not supported.", nameof(server));

        var arguments = (server.Arguments ?? []).Select(value => value ?? string.Empty).ToArray();
        if (arguments.Length > 64 || arguments.Any(value => value.Length > 2000 || value.Contains('\0')))
            throw new ArgumentException("MCP server arguments must contain at most 64 values of up to 2,000 characters.", nameof(server));
        var environment = NormalizeEnvironmentMap(server.EnvironmentVariables, headerNames: false);
        var headers = NormalizeEnvironmentMap(server.HeaderEnvironmentVariables, headerNames: true);
        ValidateTimeout(server.StartupTimeoutMs, MaxStartupOrCatalogTimeoutMs, "startup");
        ValidateTimeout(server.CatalogTimeoutMs, MaxStartupOrCatalogTimeoutMs, "catalog");
        ValidateTimeout(server.ExecutionTimeoutMs, MaxExecutionTimeoutMs, "execution");
        var workingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? string.Empty : Path.GetFullPath(server.WorkingDirectory.Trim());
        var command = (server.Command ?? string.Empty).Trim();
        var url = (server.Url ?? string.Empty).Trim();
        var oauthEnabled = server.OAuthEnabled ?? server.Transport == McpServerTransportKind.Http;
        var oauthClientId = (server.OAuthClientId ?? string.Empty).Trim();
        var oauthSecretEnvironmentVariable = (server.OAuthClientSecretEnvironmentVariable ?? string.Empty).Trim();
        var oauthScopes = (server.OAuthScopes ?? []).Select(scope => (scope ?? string.Empty).Trim()).Where(scope => scope.Length > 0).ToArray();

        if (oauthClientId.Length > 512 || oauthClientId.Any(char.IsControl))
            throw new ArgumentException("MCP OAuth client IDs must be at most 512 printable characters.");
        if (oauthSecretEnvironmentVariable.Length > 200 || oauthSecretEnvironmentVariable.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("MCP OAuth client secrets must be referenced by an environment variable name, never saved as a value.");
        if (oauthScopes.Length > 32 || oauthScopes.Any(scope => scope.Length > 200 || scope.Any(char.IsControl)))
            throw new ArgumentException("MCP OAuth supports at most 32 printable scopes of up to 200 characters each.");

        if (server.Transport == McpServerTransportKind.Stdio)
        {
            if (oauthEnabled || oauthClientId.Length > 0 || oauthSecretEnvironmentVariable.Length > 0 || oauthScopes.Length > 0)
                throw new ArgumentException("OAuth configuration can only be set for HTTP MCP servers.");
            if (command.Length is 0 or > 1000 || command.Contains('\0')) throw new ArgumentException("A local MCP server needs a command.", nameof(server));
            if (url.Length > 0 || headers.Count > 0) throw new ArgumentException("HTTP URL and headers can only be set for HTTP MCP servers.", nameof(server));
        }
        else
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                throw new ArgumentException("An HTTP MCP server needs an absolute HTTP or HTTPS URL without embedded credentials, query strings, or fragments. Use an environment-backed header mapping for authentication.", nameof(server));
            if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
                throw new ArgumentException("MCP servers on non-loopback hosts must use HTTPS; plain HTTP is limited to loopback endpoints.", nameof(server));
            if (command.Length > 0 || arguments.Length > 0 || workingDirectory.Length > 0 || environment.Count > 0)
                throw new ArgumentException("Command, arguments, working directory, and process environment can only be set for stdio MCP servers.", nameof(server));
        }

        return server with
        {
            Id = id,
            Name = name,
            Command = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Url = url,
            EnvironmentVariables = environment,
            HeaderEnvironmentVariables = headers,
            OAuthEnabled = oauthEnabled,
            OAuthClientId = oauthClientId,
            OAuthClientSecretEnvironmentVariable = oauthSecretEnvironmentVariable,
            OAuthScopes = oauthScopes
        };
    }

    /// <summary>
    /// Returns a stable, non-secret fingerprint for the configured MCP server target. Saved
    /// project Allow rules use this so changing the endpoint or process configuration requires
    /// a fresh approval. Secret values are deliberately excluded; only their environment
    /// variable references are included.
    /// </summary>
    public static string CreatePermissionFingerprint(McpServerConfiguration server)
    {
        var normalized = NormalizeAndValidate(server);
        var identity = new
        {
            normalized.Transport,
            Url = normalized.Transport == McpServerTransportKind.Http
                ? new Uri(normalized.Url, UriKind.Absolute).AbsoluteUri : "",
            normalized.Command,
            Arguments = normalized.Arguments ?? [],
            normalized.WorkingDirectory,
            EnvironmentVariables = NormalizeMappings(normalized.EnvironmentVariables, caseInsensitiveKeys: false),
            HeaderEnvironmentVariables = NormalizeMappings(normalized.HeaderEnvironmentVariables, caseInsensitiveKeys: true),
            normalized.OAuthEnabled,
            normalized.OAuthClientId,
            normalized.OAuthClientSecretEnvironmentVariable,
            OAuthScopes = (normalized.OAuthScopes ?? []).OrderBy(value => value, StringComparer.Ordinal).ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        static KeyValuePair<string, string>[] NormalizeMappings(IReadOnlyDictionary<string, string>? mappings, bool caseInsensitiveKeys) =>
            (mappings ?? new Dictionary<string, string>())
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new KeyValuePair<string, string>(caseInsensitiveKeys ? pair.Key.ToLowerInvariant() : pair.Key, pair.Value))
                .ToArray();
    }

    private static void ValidateTimeout(int? timeoutMs, int maximum, string phase)
    {
        if (timeoutMs is not null && (timeoutMs < MinTimeoutMs || timeoutMs > maximum))
            throw new ArgumentException($"MCP {phase} timeout must be between {MinTimeoutMs} and {maximum} milliseconds.");
    }

    private static McpServerConfiguration InvalidEntry(int index) => new(
        $"invalid-{index + 1}", $"Invalid MCP entry {index + 1}", McpServerTransportKind.Stdio, true);

    private static McpServerConfiguration Clone(McpServerConfiguration? server) => server is null ? null! : server with
    {
        Arguments = server.Arguments?.ToArray() ?? [],
        EnvironmentVariables = new Dictionary<string, string>(server.EnvironmentVariables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        HeaderEnvironmentVariables = new Dictionary<string, string>(server.HeaderEnvironmentVariables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        OAuthScopes = server.OAuthScopes?.ToArray() ?? []
    };

    private static Dictionary<string, string> NormalizeEnvironmentMap(IReadOnlyDictionary<string, string>? source, bool headerNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        if (source.Count > 32) throw new ArgumentException("MCP configuration supports at most 32 environment mappings.");
        foreach (var (key, value) in source)
        {
            var normalizedKey = (key ?? string.Empty).Trim();
            var variableName = (value ?? string.Empty).Trim();
            if (normalizedKey.Length is 0 or > 100 || normalizedKey.Any(c => char.IsControl(c) || c == '='))
                throw new ArgumentException("MCP environment names and HTTP header names must be valid printable names.");
            if (headerNames && !HttpHeaderNamePattern.IsMatch(normalizedKey))
                throw new ArgumentException("MCP HTTP header names must use valid HTTP token characters.");
            if (variableName.Length is 0 or > 200 || variableName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new ArgumentException("MCP secrets must be referenced by an environment variable name, never saved as a value.");
            if (headerNames && normalizedKey.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The Host header is managed by the HTTP client.");
            if (!result.TryAdd(normalizedKey, variableName)) throw new ArgumentException("MCP configuration contains a duplicate environment mapping.");
        }
        return result;
    }

    private static void ValidateCollection(IReadOnlyCollection<McpServerConfiguration> servers)
    {
        if (servers.Count > MaxServers) throw new InvalidDataException($"Codev supports at most {MaxServers} configured MCP servers.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            NormalizeAndValidate(server);
            if (!ids.Add(server.Id)) throw new InvalidDataException("MCP server IDs must be unique.");
        }
    }
}

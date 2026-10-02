using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace Codev;

/// <summary>Persists OAuth access/refresh tokens in Codev's OS credential store.</summary>
public sealed class McpOAuthTokenCache(IMcpOAuthTokenVault? vault, string account) : ITokenCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenContainer? _tokens;

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var serialized = JsonSerializer.Serialize(tokens, JsonOptions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (vault is not null) await vault.SaveTokensAsync(account, serialized).ConfigureAwait(false);
            _tokens = Clone(tokens);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens is not null) return Clone(_tokens);
            if (vault is null) return null;
            var serialized = await vault.GetTokensAsync(account).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serialized)) return null;
            try
            {
                _tokens = JsonSerializer.Deserialize<TokenContainer>(serialized, JsonOptions);
                return _tokens is null ? null : Clone(_tokens);
            }
            catch (JsonException) { return null; }
        }
        finally { _gate.Release(); }
    }

    public static string CreateAccount(McpServerConfiguration server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var identity = string.Join('\0', server.Id, new Uri(server.Url, UriKind.Absolute).AbsoluteUri, server.OAuthClientId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static TokenContainer Clone(TokenContainer token) =>
        JsonSerializer.Deserialize<TokenContainer>(JsonSerializer.Serialize(token, JsonOptions), JsonOptions)
        ?? throw new InvalidDataException("MCP OAuth tokens could not be copied.");
}

/// <summary>Opens the system browser and receives the OAuth callback on a dynamically bound loopback port.</summary>
public sealed class McpOAuthCallbackListener : IAsyncDisposable
{
    private const int MaxRequestLineCharacters = 8_192;
    private const int MaxHeaderLines = 64;
    private const int MaxHeaderCharacters = 16_384;
    private readonly TcpListener _listener;
    private readonly Func<Uri, CancellationToken, Task> _openBrowser;
    private readonly Action<string>? _status;
    private readonly string _serverName;
    private int _disposed;

    public McpOAuthCallbackListener(string serverName, Action<string>? status = null,
        Func<Uri, CancellationToken, Task>? openBrowser = null)
    {
        _serverName = serverName;
        _status = status;
        _openBrowser = openBrowser ?? OpenSystemBrowserAsync;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(4);
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        RedirectUri = new Uri($"http://127.0.0.1:{endpoint.Port}/oauth/callback", UriKind.Absolute);
    }

    public Uri RedirectUri { get; }

    public async Task<AuthorizationResult?> HandleAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RedirectUri is null || context.RedirectUri != RedirectUri)
            throw new InvalidOperationException("MCP OAuth callback redirect URI does not match Codev's loopback listener.");
        if (context.AuthorizationUri.Scheme != Uri.UriSchemeHttps &&
            !(context.AuthorizationUri.Scheme == Uri.UriSchemeHttp && context.AuthorizationUri.IsLoopback))
            throw new InvalidOperationException("MCP OAuth authorization endpoints must use HTTPS; plain HTTP is allowed only for loopback development servers.");
        if (!string.IsNullOrEmpty(context.AuthorizationUri.UserInfo))
            throw new InvalidOperationException("MCP OAuth authorization endpoints cannot contain embedded credentials.");

        _status?.Invoke($"MCP · sign in to {_serverName} in your browser to continue.");
        await _openBrowser(context.AuthorizationUri, cancellationToken).ConfigureAwait(false);

        using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var request = await ReadRequestAsync(client.GetStream(), cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteResponseAsync(client.GetStream(), success: false, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var values = ParseQuery(request.Query);
        var code = Single(values, "code");
        var state = Single(values, "state");
        var issuer = Single(values, "iss");
        var error = Single(values, "error");
        var success = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(state);
        await WriteResponseAsync(client.GetStream(), success, cancellationToken).ConfigureAwait(false);
        if (!success) return null;
        return new AuthorizationResult { Code = code, State = state, Iss = issuer };
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private async Task<Uri?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true);
        var requestLine = await ReadBoundedLineAsync(reader, MaxRequestLineCharacters, cancellationToken).ConfigureAwait(false);
        if (requestLine is null) return null;
        var totalHeaderCharacters = requestLine.Length + 2;
        var terminated = false;
        for (var count = 0; count < MaxHeaderLines; count++)
        {
            var header = await ReadBoundedLineAsync(reader, MaxRequestLineCharacters, cancellationToken).ConfigureAwait(false);
            if (header is null) return null;
            totalHeaderCharacters += header.Length + 2;
            if (totalHeaderCharacters > MaxHeaderCharacters) return null;
            if (header.Length == 0) { terminated = true; break; }
        }
        if (!terminated) return null;

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        if (!Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var requestUri) || requestUri.AbsolutePath != "/oauth/callback") return null;
        return requestUri;
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maxCharacters,
        CancellationToken cancellationToken)
    {
        var line = new StringBuilder(Math.Min(maxCharacters, 256));
        var character = new char[1];
        var carriageReturnSeen = false;
        while (true)
        {
            var count = await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return null;
            if (character[0] == '\n') return carriageReturnSeen ? line.ToString() : null;
            if (carriageReturnSeen) return null;
            if (character[0] == '\r') { carriageReturnSeen = true; continue; }
            if (line.Length >= maxCharacters) return null;
            line.Append(character[0]);
        }
    }

    private static Dictionary<string, List<string>> ParseQuery(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Decode(separator < 0 ? part : part[..separator]);
            var value = Decode(separator < 0 ? string.Empty : part[(separator + 1)..]);
            if (!result.TryGetValue(key, out var values)) result.Add(key, values = []);
            values.Add(value);
        }
        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string? Single(Dictionary<string, List<string>> values, string key) =>
        values.TryGetValue(key, out var matches) && matches.Count == 1 ? matches[0] : null;

    private static async Task WriteResponseAsync(NetworkStream stream, bool success, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(success
            ? "<!doctype html><meta charset=utf-8><title>Codev sign-in</title><h1>Sign-in complete</h1><p>You can close this tab and return to Codev.</p>"
            : "<!doctype html><meta charset=utf-8><title>Codev sign-in</title><h1>Sign-in did not complete</h1><p>Return to Codev and try again.</p>");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task OpenSystemBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

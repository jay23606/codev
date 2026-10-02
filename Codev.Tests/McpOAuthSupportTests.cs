using Codev;
using ModelContextProtocol.Authentication;
using System.Net;
using System.Net.Sockets;

namespace Codev.Tests;

public sealed class McpOAuthSupportTests
{
    [Fact]
    public void Token_account_is_stable_for_server_and_changes_with_resource_or_client_identity()
    {
        var server = new McpServerConfiguration("docs", "Docs", McpServerTransportKind.Http, true,
            Url: "https://example.test/mcp");

        var account = McpOAuthTokenCache.CreateAccount(server);

        Assert.Equal(account, McpOAuthTokenCache.CreateAccount(server with { Name = "Renamed" }));
        Assert.NotEqual(account, McpOAuthTokenCache.CreateAccount(server with { Url = "https://other.test/mcp" }));
        Assert.NotEqual(account, McpOAuthTokenCache.CreateAccount(server with { OAuthClientId = "configured-client" }));
        Assert.Matches("^[0-9a-f]{64}$", account);
    }

    [Fact]
    public async Task Token_cache_keeps_an_ephemeral_copy_when_no_vault_is_available()
    {
        var cache = new McpOAuthTokenCache(null, new string('c', 64));
        await cache.StoreTokensAsync(new TokenContainer { TokenType = "Bearer", AccessToken = "ephemeral", ObtainedAt = DateTimeOffset.UtcNow }, CancellationToken.None);

        var restored = await cache.GetTokensAsync(CancellationToken.None);

        Assert.Equal("ephemeral", restored?.AccessToken);
    }

    [Fact]
    public async Task Loopback_callback_returns_code_state_and_issuer_and_only_static_response_content()
    {
        HttpStatusCode? browserResponse = null;
        Uri? callbackBase = null;
        Task? responseTask = null;
        await using var listener = new McpOAuthCallbackListener("Docs", openBrowser: (_, cancellationToken) =>
        {
            responseTask = Task.Run(async () =>
            {
                using var http = new HttpClient();
                var callback = new Uri(callbackBase!.AbsoluteUri + "?code=a%2Bb&state=csrf-value&iss=https%3A%2F%2Fidentity.example.test");
                using var response = await http.GetAsync(callback, cancellationToken);
                browserResponse = response.StatusCode;
            }, cancellationToken);
            return Task.CompletedTask;
        });
        callbackBase = listener.RedirectUri;
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://identity.example.test/authorize"),
            RedirectUri = listener.RedirectUri
        };

        var result = await listener.HandleAsync(context, CancellationToken.None);
        await responseTask!;

        Assert.Equal(HttpStatusCode.OK, browserResponse);
        Assert.Equal("a+b", result?.Code);
        Assert.Equal("csrf-value", result?.State);
        Assert.Equal("https://identity.example.test", result?.Iss);
    }

    [Fact]
    public async Task Loopback_callback_rejects_an_oversized_request_line_without_reading_it_unbounded()
    {
        Uri? callbackBase = null;
        TcpClient? client = null;
        Task<string>? responseTask = null;
        await using var listener = new McpOAuthCallbackListener("Docs", openBrowser: async (_, cancellationToken) =>
        {
            client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, callbackBase!.Port, cancellationToken);
            var stream = client.GetStream();
            await stream.WriteAsync(Enumerable.Repeat((byte)'A', 8_193).ToArray(), cancellationToken);
            responseTask = Task.Run(async () =>
            {
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(cancellationToken);
            }, cancellationToken);
        });
        callbackBase = listener.RedirectUri;

        try
        {
            var result = await listener.HandleAsync(new AuthorizationCallbackContext
            {
                AuthorizationUri = new Uri("https://identity.example.test/authorize"),
                RedirectUri = listener.RedirectUri
            }, CancellationToken.None);

            var response = await responseTask!;
            Assert.Null(result);
            Assert.StartsWith("HTTP/1.1 400 Bad Request\r\n", response, StringComparison.Ordinal);
        }
        finally { client?.Dispose(); }
    }

    [Theory]
    [InlineData("http://identity.example.test/authorize")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@identity.example.test/authorize")]
    public async Task Callback_rejects_insecure_or_credential_bearing_authorization_urls(string authorizationUrl)
    {
        var browserOpened = false;
        await using var listener = new McpOAuthCallbackListener("Docs", openBrowser: (_, _) =>
        {
            browserOpened = true;
            return Task.CompletedTask;
        });
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri(authorizationUrl),
            RedirectUri = listener.RedirectUri
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            listener.HandleAsync(context, CancellationToken.None));
        Assert.False(browserOpened);
    }
}

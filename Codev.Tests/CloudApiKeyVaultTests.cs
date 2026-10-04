using Codev;
using ModelContextProtocol.Authentication;

namespace Codev.Tests;

public sealed class CloudApiKeyVaultTests
{
    [Fact]
    public async Task Key_survives_new_vault_instances_and_is_scoped_by_provider()
    {
        var store = new MemoryCredentialStore();
        var firstSession = new CloudApiKeyVault(() => store);
        var secondSession = new CloudApiKeyVault(() => store);

        await firstSession.SaveAsync(CloudModelProviders.OpenAI, "  openai-secret  ");
        await firstSession.SaveAsync(CloudModelProviders.Anthropic, "claude-secret");

        Assert.Equal("openai-secret", await secondSession.GetAsync(CloudModelProviders.OpenAI));
        Assert.Equal("claude-secret", await secondSession.GetAsync(CloudModelProviders.Anthropic));
    }

    [Fact]
    public async Task Redirected_data_root_uses_a_separate_credential_vault_namespace()
    {
        var store = new MemoryCredentialStore();
        var normalProfile = new CloudApiKeyVault(() => store);
        var isolatedProfile = new CloudApiKeyVault(() => store, Path.Combine(Path.GetTempPath(), "codev-isolated-vault-profile"));
        await normalProfile.SaveAsync(CloudModelProviders.OpenAI, "normal-profile-key");

        Assert.Null(await isolatedProfile.GetAsync(CloudModelProviders.OpenAI));
        await isolatedProfile.SaveAsync(CloudModelProviders.OpenAI, "isolated-profile-key");

        Assert.Equal("normal-profile-key", await normalProfile.GetAsync(CloudModelProviders.OpenAI));
        Assert.Equal("isolated-profile-key", await isolatedProfile.GetAsync(CloudModelProviders.OpenAI));

        var oauthAccount = new string('c', 64);
        await normalProfile.SaveTokensAsync(oauthAccount, "normal-profile-token");
        Assert.Null(await isolatedProfile.GetTokensAsync(oauthAccount));
        await isolatedProfile.SaveTokensAsync(oauthAccount, "isolated-profile-token");
        Assert.Equal("normal-profile-token", await normalProfile.GetTokensAsync(oauthAccount));
        Assert.Equal("isolated-profile-token", await isolatedProfile.GetTokensAsync(oauthAccount));
    }

    [Fact]
    public async Task Save_replaces_and_remove_forgets_only_selected_provider()
    {
        var store = new MemoryCredentialStore();
        var vault = new CloudApiKeyVault(() => store);
        await vault.SaveAsync(CloudModelProviders.OpenAI, "old-key");
        await vault.SaveAsync(CloudModelProviders.Anthropic, "claude-key");

        await vault.SaveAsync(CloudModelProviders.OpenAI, "new-key");
        Assert.True(await vault.RemoveAsync(CloudModelProviders.OpenAI));

        Assert.Null(await vault.GetAsync(CloudModelProviders.OpenAI));
        Assert.Equal("claude-key", await vault.GetAsync(CloudModelProviders.Anthropic));
        Assert.False(await vault.RemoveAsync(CloudModelProviders.OpenAI));
    }

    [Fact]
    public async Task Mcp_oauth_tokens_round_trip_through_the_os_credential_vault()
    {
        var store = new MemoryCredentialStore();
        var vault = new CloudApiKeyVault(() => store);
        var account = new string('a', 64);
        await vault.SaveTokensAsync(account, "{\"access_token\":\"secret-token\"}");

        Assert.Equal("{\"access_token\":\"secret-token\"}", await vault.GetTokensAsync(account));
        Assert.True(await vault.RemoveTokensAsync(account));
        Assert.Null(await vault.GetTokensAsync(account));
    }

    [Fact]
    public async Task Mcp_oauth_token_cache_persists_client_registration_and_refresh_credentials()
    {
        var vault = new CloudApiKeyVault(() => new MemoryCredentialStore());
        var account = new string('b', 64);
        var first = new McpOAuthTokenCache(vault, account);
        await first.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer", AccessToken = "access", RefreshToken = "refresh", ExpiresIn = 300,
            ClientId = "registered-client", ClientSecret = "registered-secret", ObtainedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var restored = await new McpOAuthTokenCache(vault, account).GetTokensAsync(CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("access", restored.AccessToken);
        Assert.Equal("refresh", restored.RefreshToken);
        Assert.Equal("registered-client", restored.ClientId);
        Assert.Equal("registered-secret", restored.ClientSecret);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    public async Task Mcp_oauth_vault_rejects_non_digest_account_ids(string account)
    {
        var vault = new CloudApiKeyVault(() => new MemoryCredentialStore());

        await Assert.ThrowsAsync<ArgumentException>(() => vault.GetTokensAsync(account));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.SaveTokensAsync(account, "token"));
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("")]
    public async Task Rejects_non_hosted_provider_keys(string provider)
    {
        var vault = new CloudApiKeyVault(() => new MemoryCredentialStore());

        await Assert.ThrowsAsync<ArgumentException>(() => vault.GetAsync(provider));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.SaveAsync(provider, "secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.RemoveAsync(provider));
    }

    [Fact]
    public async Task Rejects_empty_key_and_never_calls_store()
    {
        var store = new MemoryCredentialStore();
        var vault = new CloudApiKeyVault(() => store);

        await Assert.ThrowsAsync<ArgumentException>(() => vault.SaveAsync(CloudModelProviders.OpenAI, "  "));
        Assert.Equal(0, store.WriteCount);
    }

    private sealed class MemoryCredentialStore : ICloudApiKeyStoreBackend
    {
        private readonly Dictionary<(string Service, string Account), string> _values = [];
        public int WriteCount { get; private set; }

        public IReadOnlyList<string> GetAccounts(string target) => _values.Keys
            .Where(key => key.Service == target).Select(key => key.Account).ToArray();

        public string? Get(string target, string account)
        {
            return _values.TryGetValue((target, account), out var secret) ? secret : null;
        }

        public void AddOrUpdate(string target, string account, string secret)
        {
            _values[(target, account)] = secret;
            WriteCount++;
        }

        public bool Remove(string target, string account) => _values.Remove((target, account));
    }
}

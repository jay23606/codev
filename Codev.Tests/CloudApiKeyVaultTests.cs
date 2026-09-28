using Codev;

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

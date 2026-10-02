using GitCredentialManager;

namespace Codev;

public interface ICloudApiKeyVault
{
    Task<string?> GetAsync(string provider);
    Task SaveAsync(string provider, string apiKey);
    Task<bool> RemoveAsync(string provider);
}

public interface IMcpOAuthTokenVault
{
    Task<string?> GetTokensAsync(string account);
    Task SaveTokensAsync(string account, string tokens);
    Task<bool> RemoveTokensAsync(string account);
}

public interface ICloudApiKeyStoreBackend
{
    IReadOnlyList<string> GetAccounts(string target);
    string? Get(string target, string account);
    void AddOrUpdate(string target, string account, string secret);
    bool Remove(string target, string account);
}

/// <summary>Stores hosted-provider keys in the operating system's native credential store.</summary>
public sealed class CloudApiKeyVault : ICloudApiKeyVault, IMcpOAuthTokenVault
{
    private const string ApplicationName = "Codev";
    private const string CredentialTarget = "https://codev.local/hosted-model-api";
    private const string McpOAuthCredentialTarget = "https://codev.local/mcp-oauth";
    private static readonly object EnvironmentLock = new();
    private readonly Func<ICloudApiKeyStoreBackend> _storeFactory;
    private readonly string _credentialTarget;
    private readonly string _mcpOAuthCredentialTarget;
    private ICloudApiKeyStoreBackend? _store;
    private readonly object _storeLock = new();

    public CloudApiKeyVault() : this(CreateNativeCredentialStore,
        Environment.GetEnvironmentVariable(CodevDataPaths.RootEnvironmentVariable)) { }

    public CloudApiKeyVault(Func<ICloudApiKeyStoreBackend> storeFactory) : this(storeFactory, configuredDataRoot: null) { }

    internal CloudApiKeyVault(Func<ICloudApiKeyStoreBackend> storeFactory, string? configuredDataRoot)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _credentialTarget = CodevDataPaths.ScopeCredentialTarget(CredentialTarget, configuredDataRoot);
        _mcpOAuthCredentialTarget = CodevDataPaths.ScopeCredentialTarget(McpOAuthCredentialTarget, configuredDataRoot);
    }

    public Task<string?> GetAsync(string provider) => Task.Run(() =>
    {
        var account = NormalizeProvider(provider);
        lock (_storeLock)
        {
            var store = GetStore();
            if (!store.GetAccounts(_credentialTarget).Contains(account, StringComparer.OrdinalIgnoreCase)) return null;
            return store.Get(_credentialTarget, account);
        }
    });

    public Task SaveAsync(string provider, string apiKey) => Task.Run(() =>
    {
        var account = NormalizeProvider(provider);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("The API key cannot be empty.", nameof(apiKey));
        lock (_storeLock) GetStore().AddOrUpdate(_credentialTarget, account, apiKey.Trim());
    });

    public Task<bool> RemoveAsync(string provider) => Task.Run(() =>
    {
        var account = NormalizeProvider(provider);
        lock (_storeLock) return GetStore().Remove(_credentialTarget, account);
    });

    public Task<string?> GetTokensAsync(string account) => Task.Run(() =>
    {
        ValidateMcpOAuthAccount(account);
        lock (_storeLock)
        {
            var store = GetStore();
            if (!store.GetAccounts(_mcpOAuthCredentialTarget).Contains(account, StringComparer.Ordinal)) return null;
            return store.Get(_mcpOAuthCredentialTarget, account);
        }
    });

    public Task SaveTokensAsync(string account, string tokens) => Task.Run(() =>
    {
        ValidateMcpOAuthAccount(account);
        if (string.IsNullOrWhiteSpace(tokens) || tokens.Length > 16_000)
            throw new ArgumentException("MCP OAuth token data must contain 1–16,000 characters.", nameof(tokens));
        lock (_storeLock) GetStore().AddOrUpdate(_mcpOAuthCredentialTarget, account, tokens);
    });

    public Task<bool> RemoveTokensAsync(string account) => Task.Run(() =>
    {
        ValidateMcpOAuthAccount(account);
        lock (_storeLock) return GetStore().Remove(_mcpOAuthCredentialTarget, account);
    });

    private static void ValidateMcpOAuthAccount(string account)
    {
        if (account.Length is not 64 || account.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("MCP OAuth credential identifiers must be a 64-character hexadecimal digest.", nameof(account));
    }

    private ICloudApiKeyStoreBackend GetStore() => _store ??= _storeFactory();

    private static string NormalizeProvider(string provider) => provider switch
    {
        CloudModelProviders.OpenAI => CloudModelProviders.OpenAI,
        CloudModelProviders.Anthropic => CloudModelProviders.Anthropic,
        _ => throw new ArgumentException("Only OpenAI and Anthropic API keys can be stored.", nameof(provider))
    };

    private static ICloudApiKeyStoreBackend CreateNativeCredentialStore()
    {
        var backend = OperatingSystem.IsWindows() ? "wincredman"
            : OperatingSystem.IsMacOS() ? "keychain"
            : OperatingSystem.IsLinux() ? "secretservice"
            : throw new PlatformNotSupportedException("Codev API keys require Windows Credential Manager, macOS Keychain, or Linux Secret Service.");

        // Force a native protected store regardless of a user's global Git credential-store setting.
        // In particular, never let a global plaintext-store setting affect Codev secrets.
        lock (EnvironmentLock)
        {
            const string variable = "GCM_CREDENTIAL_STORE";
            var previous = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, backend, EnvironmentVariableTarget.Process);
            try { return new GcmCredentialStoreBackend(CredentialManager.Create(ApplicationName)); }
            finally { Environment.SetEnvironmentVariable(variable, previous, EnvironmentVariableTarget.Process); }
        }
    }

    private sealed class GcmCredentialStoreBackend(ICredentialStore store) : ICloudApiKeyStoreBackend
    {
        public IReadOnlyList<string> GetAccounts(string target) => store.GetAccounts(target).ToArray();
        public string? Get(string target, string account) => store.Get(target, account).Password;
        public void AddOrUpdate(string target, string account, string secret) => store.AddOrUpdate(target, account, secret);
        public bool Remove(string target, string account) => store.Remove(target, account);
    }
}

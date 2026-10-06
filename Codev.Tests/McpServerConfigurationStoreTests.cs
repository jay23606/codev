using Codev;

namespace Codev.Tests;

public sealed class McpServerConfigurationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-mcp-config-tests", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_root, "mcp-servers.json");

    [Fact]
    public async Task Saves_user_level_stdio_and_http_configuration_without_secret_values()
    {
        var store = new McpServerConfigurationStore(PathName);
        var servers = new[]
        {
            new McpServerConfiguration("github", "GitHub", McpServerTransportKind.Stdio, true,
                Command: "npx", Arguments: ["-y", "@example/github-mcp"],
                EnvironmentVariables: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "CODEV_GITHUB_TOKEN" }),
            new McpServerConfiguration("docs", "Docs", McpServerTransportKind.Http, false,
                Url: "https://example.test/mcp",
                HeaderEnvironmentVariables: new Dictionary<string, string> { ["Authorization"] = "CODEV_DOCS_TOKEN" },
                StartupTimeoutMs: 45_000, CatalogTimeoutMs: 60_000, ExecutionTimeoutMs: 180_000)
        };

        await store.SaveAsync(servers);
        var reloaded = await new McpServerConfigurationStore(PathName).LoadAsync();

        Assert.Equal(2, reloaded.Count);
        Assert.Equal("CODEV_GITHUB_TOKEN", reloaded[0].EnvironmentVariables!["GITHUB_TOKEN"]);
        Assert.Equal("CODEV_DOCS_TOKEN", reloaded[1].HeaderEnvironmentVariables!["Authorization"]);
        Assert.Equal(45_000, reloaded[1].StartupTimeoutMs);
        Assert.Equal(60_000, reloaded[1].CatalogTimeoutMs);
        Assert.Equal(180_000, reloaded[1].ExecutionTimeoutMs);
        var storedText = await File.ReadAllTextAsync(PathName);
        Assert.DoesNotContain("secret-value", storedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CODEV_GITHUB_TOKEN", storedText);
    }

    [Theory]
    [InlineData("https://user:secret@example.test/mcp")]
    [InlineData("https://example.test/mcp?token=secret")]
    [InlineData("https://example.test/mcp#secret")]
    [InlineData("file:///tmp/mcp")]
    [InlineData("http://example.test/mcp")]
    [InlineData("http://192.168.1.20:8080/mcp")]
    public void Rejects_invalid_http_endpoints_that_could_leak_credentials_or_use_an_unsupported_scheme(string url)
    {
        var configuration = new McpServerConfiguration("remote", "Remote", McpServerTransportKind.Http, true, Url: url);

        Assert.Throws<ArgumentException>(() => McpServerConfigurationStore.NormalizeAndValidate(configuration));
    }

    [Fact]
    public void Requires_environment_variable_references_instead_of_secret_values()
    {
        var configuration = new McpServerConfiguration("local", "Local", McpServerTransportKind.Stdio, true,
            Command: "node", EnvironmentVariables: new Dictionary<string, string> { ["TOKEN"] = "actual-secret-value" });

        Assert.Throws<ArgumentException>(() => McpServerConfigurationStore.NormalizeAndValidate(configuration));
    }

    [Fact]
    public void Stdio_transport_does_not_inherit_arbitrary_process_environment()
    {
        var allowedVariable = "CODEV_MCP_ALLOWED_" + Guid.NewGuid().ToString("N");
        var unrelatedVariable = "CODEV_MCP_UNRELATED_" + Guid.NewGuid().ToString("N");
        var previousAllowed = Environment.GetEnvironmentVariable(allowedVariable);
        var previousUnrelated = Environment.GetEnvironmentVariable(unrelatedVariable);
        Environment.SetEnvironmentVariable(allowedVariable, "mapped-test-value");
        Environment.SetEnvironmentVariable(unrelatedVariable, "must-not-be-forwarded");
        try
        {
            var server = new McpServerConfiguration("local", "Local", McpServerTransportKind.Stdio, true,
                Command: "node", EnvironmentVariables: new Dictionary<string, string> { ["MCP_TOKEN"] = allowedVariable });

            var options = McpCodeTaskSession.CreateStdioTransportOptions(server);

            Assert.False(options.InheritEnvironmentVariables);
            Assert.Equal("mapped-test-value", options.EnvironmentVariables!["MCP_TOKEN"]);
            Assert.DoesNotContain(unrelatedVariable, options.EnvironmentVariables.Keys);
            Assert.DoesNotContain("must-not-be-forwarded", options.EnvironmentVariables.Values);
        }
        finally
        {
            Environment.SetEnvironmentVariable(allowedVariable, previousAllowed);
            Environment.SetEnvironmentVariable(unrelatedVariable, previousUnrelated);
        }
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("X:Injected")]
    [InlineData("X/Trace")]
    [InlineData("Héader")]
    public void Rejects_invalid_http_header_field_names(string headerName)
    {
        var configuration = new McpServerConfiguration("remote", "Remote", McpServerTransportKind.Http, true,
            Url: "https://example.test/mcp",
            HeaderEnvironmentVariables: new Dictionary<string, string> { [headerName] = "MCP_TOKEN" });

        Assert.Throws<ArgumentException>(() => McpServerConfigurationStore.NormalizeAndValidate(configuration));
    }

    [Fact]
    public void Accepts_valid_http_header_token_characters()
    {
        var configuration = new McpServerConfiguration("remote", "Remote", McpServerTransportKind.Http, true,
            Url: "https://example.test/mcp",
            HeaderEnvironmentVariables: new Dictionary<string, string> { ["X-Trace_Id~"] = "MCP_TOKEN" });

        var normalized = McpServerConfigurationStore.NormalizeAndValidate(configuration);

        Assert.Contains("X-Trace_Id~", normalized.HeaderEnvironmentVariables!.Keys);
    }

    [Fact]
    public void Http_oauth_defaults_on_and_stdio_defaults_off_while_explicit_http_opt_out_is_preserved()
    {
        var remote = McpServerConfigurationStore.NormalizeAndValidate(
            new McpServerConfiguration("remote", "Remote", McpServerTransportKind.Http, true, Url: "https://example.test/mcp"));
        var local = McpServerConfigurationStore.NormalizeAndValidate(
            new McpServerConfiguration("local", "Local", McpServerTransportKind.Stdio, false, Command: "node"));
        var headerAuth = McpServerConfigurationStore.NormalizeAndValidate(
            new McpServerConfiguration("headers", "Headers", McpServerTransportKind.Http, true,
                Url: "https://example.test/mcp", OAuthEnabled: false));

        Assert.True(remote.OAuthEnabled);
        Assert.False(local.OAuthEnabled);
        Assert.False(headerAuth.OAuthEnabled);
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/mcp")]
    [InlineData("http://localhost:3000/mcp")]
    [InlineData("http://[::1]:3000/mcp")]
    public void Allows_plain_http_for_loopback_mcp_servers(string url)
    {
        var configuration = McpServerConfigurationStore.NormalizeAndValidate(
            new McpServerConfiguration("local", "Local", McpServerTransportKind.Http, true, Url: url));

        Assert.True(configuration.OAuthEnabled);
    }

    [Fact]
    public void OAuth_secret_must_be_an_environment_variable_reference_and_scopes_are_bounded()
    {
        var configuration = new McpServerConfiguration("remote", "Remote", McpServerTransportKind.Http, true,
            Url: "https://example.test/mcp", OAuthClientSecretEnvironmentVariable: "MCP_CLIENT_SECRET", OAuthScopes: ["read", "write"]);

        var normalized = McpServerConfigurationStore.NormalizeAndValidate(configuration);
        Assert.Equal(["read", "write"], normalized.OAuthScopes);
        Assert.Throws<ArgumentException>(() => McpServerConfigurationStore.NormalizeAndValidate(configuration with
        {
            OAuthClientSecretEnvironmentVariable = "actual-secret-value"
        }));
    }

    [Theory]
    [InlineData(999, null, null)]
    [InlineData(null, 120_001, null)]
    [InlineData(null, null, 600_001)]
    public void Rejects_timeout_values_outside_bounded_ranges(int? startup, int? catalog, int? execution)
    {
        var configuration = new McpServerConfiguration("local", "Local", McpServerTransportKind.Stdio, true,
            Command: "node", StartupTimeoutMs: startup, CatalogTimeoutMs: catalog, ExecutionTimeoutMs: execution);

        Assert.Throws<ArgumentException>(() => McpServerConfigurationStore.NormalizeAndValidate(configuration));
    }

    [Fact]
    public async Task Older_configuration_without_timeout_properties_keeps_safe_defaults()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PathName, """[{"id":"docs","name":"Docs","transport":"Http","enabled":true,"url":"https://example.test/mcp"}]""");

        var server = Assert.Single(await new McpServerConfigurationStore(PathName).LoadAsync());

        Assert.Null(server.StartupTimeoutMs);
        Assert.Null(server.CatalogTimeoutMs);
        Assert.Null(server.ExecutionTimeoutMs);
        Assert.Equal(McpServerConfigurationStore.DefaultExecutionTimeoutMs,
            McpServerConfigurationStore.NormalizeAndValidate(server).ExecutionTimeoutMs ?? McpServerConfigurationStore.DefaultExecutionTimeoutMs);
    }

    [Fact]
    public async Task Loaded_configuration_is_a_deep_snapshot_and_edits_require_save()
    {
        Directory.CreateDirectory(_root);
        var store = new McpServerConfigurationStore(PathName);
        await store.SaveAsync([
            new McpServerConfiguration("local", "Local", McpServerTransportKind.Stdio, true,
                Command: "node", Arguments: ["server.js"], EnvironmentVariables: new Dictionary<string, string> { ["TOKEN"] = "CODEV_TOKEN" })
        ]);
        var loaded = Assert.Single(await store.LoadAsync());
        ((string[])loaded.Arguments!)[0] = "changed.js";
        ((Dictionary<string, string>)loaded.EnvironmentVariables!)["TOKEN"] = "CHANGED_TOKEN";

        var reloaded = Assert.Single(await store.LoadAsync());

        Assert.Equal("server.js", Assert.Single(reloaded.Arguments!));
        Assert.Equal("CODEV_TOKEN", reloaded.EnvironmentVariables!["TOKEN"]);
        Assert.Contains("server.js", await File.ReadAllTextAsync(PathName));
    }

    [Fact]
    public async Task Invalid_saved_configuration_is_reported_without_overwriting_the_file()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PathName, "{ invalid json");
        var before = await File.ReadAllTextAsync(PathName);

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => new McpServerConfigurationStore(PathName).LoadAsync());

        Assert.Equal(before, await File.ReadAllTextAsync(PathName));
    }

    [Fact]
    public async Task Invalid_entry_is_isolated_while_other_user_servers_load()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PathName,
            """[{"id":"bad","name":"Bad URL","transport":"Http","enabled":true,"url":"file:///tmp/mcp"},{"id":"good","name":"Good","transport":"Stdio","enabled":false,"command":"unused"}]""");

        var servers = await new McpServerConfigurationStore(PathName).LoadAsync();
        await using var session = await McpCodeTaskSession.ConnectAsync(servers);
        var logs = string.Join("\n", session.ConnectionLog);

        Assert.Contains("Bad URL: invalid configuration", logs, StringComparison.Ordinal);
        Assert.Contains("Good: disabled.", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_entry_shape_isolated_from_valid_entries()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PathName,
            """[{"id":17,"name":false,"transport":"unknown"},{"id":"good","name":"Good","transport":"Stdio","enabled":false,"command":"unused"}]""");

        var servers = await new McpServerConfigurationStore(PathName).LoadAsync();
        await using var session = await McpCodeTaskSession.ConnectAsync(servers);
        var logs = string.Join("\n", session.ConnectionLog);

        Assert.Contains("Invalid MCP entry 1: invalid configuration", logs, StringComparison.Ordinal);
        Assert.Contains("Good: disabled.", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_duplicate_ids_and_preserves_the_previously_saved_file()
    {
        var store = new McpServerConfigurationStore(PathName);
        var original = new McpServerConfiguration("docs", "Docs", McpServerTransportKind.Http, true, Url: "https://example.test/mcp");
        await store.SaveAsync([original]);
        var before = await File.ReadAllTextAsync(PathName);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([
            original,
            original with { Name = "Duplicate" }
        ]));

        Assert.Equal(before, await File.ReadAllTextAsync(PathName));
    }

    [Fact]
    public async Task Refuses_more_than_the_supported_server_count()
    {
        var servers = Enumerable.Range(0, McpServerConfigurationStore.MaxServers + 1)
            .Select(index => new McpServerConfiguration($"server-{index}", $"Server {index}", McpServerTransportKind.Stdio, false, Command: "node"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new McpServerConfigurationStore(PathName).SaveAsync(servers));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}

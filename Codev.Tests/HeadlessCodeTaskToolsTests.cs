using System.Text.Json;
using Codev;

namespace Codev.Tests;

public sealed class HeadlessCodeTaskToolsTests
{
    [Fact]
    public void Options_default_to_ollama_read_only_and_default_endpoint()
    {
        var parsed = HeadlessCodeTaskOptions.TryParse(["--model", "qwen-test"], null, null,
            Environment.CurrentDirectory, out var options, out var error);

        Assert.True(parsed, error);
        Assert.Equal("ollama", options.Provider);
        Assert.Equal("qwen-test", options.Model);
        Assert.Equal(OllamaEndpoint.Default, options.Endpoint);
        Assert.False(options.AllowEdits);
        Assert.False(options.AllowCommands);
    }

    [Fact]
    public void Options_keep_file_edits_and_shell_commands_separate()
    {
        Assert.True(HeadlessCodeTaskOptions.TryParse(["--provider", "openai", "--model", "gpt-test", "--allow-edits", "--allow-hosted-data"],
            null, null, Environment.CurrentDirectory, out var editsOnly, out var editError), editError);
        Assert.True(editsOnly.AllowEdits);
        Assert.False(editsOnly.AllowCommands);

        Assert.True(HeadlessCodeTaskOptions.TryParse(["--model", "qwen-test", "--allow-commands"],
            null, null, Environment.CurrentDirectory, out var commandsOnly, out var commandError), commandError);
        Assert.False(commandsOnly.AllowEdits);
        Assert.True(commandsOnly.AllowCommands);
    }

    [Theory]
    [InlineData("--provider", "invalid", "Provider must be 'ollama' or 'openai'.")]
    [InlineData("--model", "--allow", "--model requires a value.")]
    public void Options_reject_invalid_provider_and_missing_option_values(string option, string value, string expectedError)
    {
        var parsed = HeadlessCodeTaskOptions.TryParse([option, value], null, "fallback-model",
            Environment.CurrentDirectory, out _, out var error);

        Assert.False(parsed);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void OpenAi_rejects_an_ollama_endpoint_override()
    {
        var parsed = HeadlessCodeTaskOptions.TryParse(["--provider", "openai", "--model", "gpt-test", "--endpoint", "http://localhost:11434"],
            null, null, Environment.CurrentDirectory, out _, out var error);

        Assert.False(parsed);
        Assert.Equal("--endpoint is supported only with --provider ollama.", error);
    }

    [Fact]
    public void OpenAi_requires_explicit_hosted_data_consent()
    {
        var denied = HeadlessCodeTaskOptions.TryParse(["--provider", "openai", "--model", "gpt-test"],
            null, null, Environment.CurrentDirectory, out _, out var error);

        Assert.False(denied);
        Assert.Contains("--allow-hosted-data", error);

        Assert.True(HeadlessCodeTaskOptions.TryParse(["--provider", "openai", "--model", "gpt-test", "--allow-hosted-data"],
            null, null, Environment.CurrentDirectory, out var allowed, out error), error);
        Assert.True(allowed.AllowHostedData);
    }

    [Fact]
    public void Remote_Ollama_endpoint_requires_explicit_sharing_consent()
    {
        var denied = HeadlessCodeTaskOptions.TryParse(["--model", "qwen-test", "--endpoint", "https://ollama.example.test"],
            null, null, Environment.CurrentDirectory, out _, out var error);

        Assert.False(denied);
        Assert.Contains("--allow-remote-endpoint", error);

        Assert.True(HeadlessCodeTaskOptions.TryParse(["--model", "qwen-test", "--endpoint", "https://ollama.example.test", "--allow-remote-endpoint"],
            null, null, Environment.CurrentDirectory, out var allowed, out error), error);
        Assert.False(OllamaEndpoint.IsLoopback(allowed.Endpoint));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Default_schema_only_exposes_read_only_project_inspection(bool useOllama)
    {
        var tools = HeadlessCodeTaskTools.Create(ShellCommandResolver.Resolve(true), useOllama);

        Assert.Equal(["list_files", "read_file", "search_files"], tools.Select(tool => ReadName(tool, useOllama)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Edit_and_command_schemas_require_separate_explicit_grants(bool useOllama)
    {
        var tools = HeadlessCodeTaskTools.Create(ShellCommandResolver.Resolve(true), useOllama,
            allowEdits: true, allowCommands: false);
        var names = tools.Select(tool => ReadName(tool, useOllama)).ToArray();

        Assert.Contains("create_file", names);
        Assert.Contains("write_file", names);
        Assert.Contains("apply_patch", names);
        Assert.DoesNotContain("run_command", names);
        Assert.DoesNotContain("verify_command", names);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Command_grant_does_not_implicitly_grant_file_edits(bool useOllama)
    {
        var tools = HeadlessCodeTaskTools.Create(ShellCommandResolver.Resolve(true), useOllama,
            allowEdits: false, allowCommands: true);
        var names = tools.Select(tool => ReadName(tool, useOllama)).ToArray();

        Assert.Contains("run_command", names);
        Assert.Contains("verify_command", names);
        Assert.DoesNotContain("create_file", names);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("apply_patch", names);
    }

    private static string ReadName(object tool, bool useOllama)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tool, JsonSerializerOptions.Web));
        return useOllama
            ? document.RootElement.GetProperty("function").GetProperty("name").GetString()!
            : document.RootElement.GetProperty("name").GetString()!;
    }
}

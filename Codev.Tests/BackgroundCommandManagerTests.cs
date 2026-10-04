using Codev;

namespace Codev.Tests;

public sealed class BackgroundCommandManagerTests
{
    [Fact]
    public void Background_tools_are_opt_in_and_obey_profile_tool_denials()
    {
        var shell = ShellCommandResolver.ResolveCurrent();
        var regular = CodeTaskToolSchemaFactory.CreateOllamaTools(shell);
        var background = CodeTaskToolSchemaFactory.CreateOllamaTools(shell, allowBackgroundCommands: true);
        var plan = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Plan");
        var restricted = CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, profile: plan, allowBackgroundCommands: true);
        var regularNames = ToolNames(regular);
        var backgroundNames = ToolNames(background);
        var restrictedNames = ToolNames(restricted);

        Assert.DoesNotContain("start_background_command", regularNames);
        Assert.Contains("start_background_command", backgroundNames);
        Assert.Contains("read_background_command", backgroundNames);
        Assert.Contains("stop_background_command", backgroundNames);
        Assert.DoesNotContain("start_background_command", restrictedNames);
        Assert.DoesNotContain("read_background_command", restrictedNames);
        Assert.DoesNotContain("stop_background_command", restrictedNames);
    }

    [Fact]
    public void Background_command_description_matches_auto_and_ask_permission_behavior()
    {
        var tool = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            CodeTaskToolSchemaFactory.CreateOllamaTools(ShellCommandResolver.ResolveCurrent(), allowBackgroundCommands: true),
            System.Text.Json.JsonSerializerOptions.Web));
        var description = tool.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("function").GetProperty("name").GetString() == "start_background_command")
            .GetProperty("function").GetProperty("description").GetString();

        Assert.NotNull(description);
        Assert.Contains("Auto runs it without approval", description, StringComparison.Ordinal);
        Assert.Contains("exact saved deny rule", description, StringComparison.Ordinal);
        Assert.Contains("Ask and Allowlist", description, StringComparison.Ordinal);
        Assert.DoesNotContain("exact command must be approved", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsandboxed", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_command_activities_are_described_as_started_and_read()
    {
        var started = new ChatMessage("assistant", "**start_background_command**\n" + UntrustedToolOutput.Format("background command", "Started id", command: "npm run dev", activity: "background_command_started"));
        var read = new ChatMessage("assistant", "**read_background_command**\n" + UntrustedToolOutput.Format("background command output", "Output", command: "npm run dev", activity: "background_command_output"));

        Assert.Equal("Started background command · npm run dev", Assert.Single(started.ToolOutputs).Summary);
        Assert.Equal("Read background command output · npm run dev", Assert.Single(read.ToolOutputs).Summary);
        Assert.Equal("Started background commands", started.CommandToolOutputsHeader);
    }

    [Fact]
    public async Task Command_output_is_bounded_and_owned_by_its_conversation()
    {
        await using var manager = new BackgroundCommandManager();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var root = Path.GetTempPath();
        var command = OperatingSystem.IsWindows()
            ? "Write-Output background-ready; Write-Output ('x' * 30000)"
            : "printf 'background-ready\\n'; head -c 30000 /dev/zero | tr '\\0' x";

        var started = await manager.StartAsync(owner, command, root, ShellCommandResolver.ResolveCurrent());
        var completed = await WaitForExitAsync(manager, owner, started.Id);

        Assert.Equal("Exited", completed.Status);
        Assert.Equal(0, completed.ExitCode);
        Assert.Contains("background-ready", completed.Output, StringComparison.Ordinal);
        Assert.True(completed.Output.Length <= BackgroundCommandManager.MaxOutputCharacters + 20);
        Assert.Contains("output truncated", completed.Output, StringComparison.Ordinal);
        Assert.Null(manager.Read(other, started.Id));
        Assert.Single(manager.List(owner));
    }

    [Fact]
    public async Task Stop_kills_a_running_command_and_its_process_tree()
    {
        await using var manager = new BackgroundCommandManager();
        var owner = Guid.NewGuid();
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";

        var started = await manager.StartAsync(owner, command, Path.GetTempPath(), ShellCommandResolver.ResolveCurrent());
        Assert.Equal("Running", started.Status);
        Assert.True(await manager.StopAsync(owner, started.Id));

        var stopped = manager.Read(owner, started.Id);
        Assert.NotNull(stopped);
        Assert.Equal("Exited", stopped.Status);
        Assert.False(await manager.StopAsync(owner, started.Id));
    }

    [Fact]
    public async Task Manager_shutdown_stops_every_running_process()
    {
        var manager = new BackgroundCommandManager();
        var owner = Guid.NewGuid();
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var started = await manager.StartAsync(owner, command, Path.GetTempPath(), ShellCommandResolver.ResolveCurrent());

        await manager.DisposeAsync();

        Assert.Equal("Exited", manager.Read(owner, started.Id)!.Status);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Canceled_start_does_not_launch_or_register_a_process()
    {
        await using var manager = new BackgroundCommandManager();
        using var cancellation = new CancellationTokenSource();
        var owner = Guid.NewGuid();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartAsync(owner,
            OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30", Path.GetTempPath(),
            ShellCommandResolver.ResolveCurrent(), cancellation.Token));

        Assert.Empty(manager.List(owner));
    }

    [Fact]
    public async Task Running_command_limit_is_enforced_per_conversation()
    {
        await using var manager = new BackgroundCommandManager();
        var owner = Guid.NewGuid();
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        try
        {
            for (var i = 0; i < BackgroundCommandManager.MaxRunningPerConversation; i++)
                await manager.StartAsync(owner, command, Path.GetTempPath(), ShellCommandResolver.ResolveCurrent());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.StartAsync(owner, command, Path.GetTempPath(), ShellCommandResolver.ResolveCurrent()));
            Assert.Contains("At most 3 background commands", exception.Message, StringComparison.Ordinal);
        }
        finally { await manager.StopConversationAsync(owner); }
    }

    [Fact]
    public async Task Stopping_commands_keep_their_running_slots_until_the_process_tree_exits()
    {
        await using var manager = new BackgroundCommandManager();
        var owner = Guid.NewGuid();
        var command = OperatingSystem.IsWindows()
            ? "while ($true) { Start-Sleep -Milliseconds 1000 }"
            : "while :; do sleep 1; done";
        using var stoppingReached = new ManualResetEventSlim();
        using var resumeShutdown = new ManualResetEventSlim();
        manager.Changed += (_, _) =>
        {
            if (!manager.List(owner).Any(item => item.Status == "Stopping")) return;
            stoppingReached.Set();
            resumeShutdown.Wait(TimeSpan.FromSeconds(10));
        };
        for (var i = 0; i < BackgroundCommandManager.MaxRunningPerConversation; i++)
            await manager.StartAsync(owner, command, Path.GetTempPath(), ShellCommandResolver.ResolveCurrent());

        var shutdown = Task.Run(() => manager.StopConversationAsync(owner));
        try
        {
            Assert.True(stoppingReached.Wait(TimeSpan.FromSeconds(10)), "Shutdown did not enter the stopping state.");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.StartAsync(owner, command, Path.GetTempPath(), ShellCommandResolver.ResolveCurrent()));
            Assert.Contains("At most 3 background commands", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            resumeShutdown.Set();
            await shutdown;
        }
    }

    private static async Task<BackgroundCommandSnapshot> WaitForExitAsync(BackgroundCommandManager manager, Guid owner, string id)
    {
        // Hosted Windows runners can take several seconds to start PowerShell
        // while other lifecycle tests are launching processes in parallel.
        // Keep a finite bound, but avoid treating ordinary runner contention
        // as a background-process failure.
        var timeout = DateTimeOffset.UtcNow.AddSeconds(30);
        BackgroundCommandSnapshot? last = null;
        while (DateTimeOffset.UtcNow < timeout)
        {
            last = manager.Read(owner, id);
            if (last?.Status == "Exited") return last;
            await Task.Delay(50);
        }
        throw new TimeoutException($"The background process did not exit within thirty seconds (last status: {last?.Status ?? "missing"}, exit code: {last?.ExitCode?.ToString() ?? "unknown"}, output length: {last?.Output.Length ?? 0}).");
    }

    private static HashSet<string> ToolNames(IEnumerable<object> schemas)
    {
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(schemas, System.Text.Json.JsonSerializerOptions.Web));
        return document.RootElement.EnumerateArray().Select(tool => tool.TryGetProperty("function", out var function)
            ? function.GetProperty("name").GetString() ?? ""
            : tool.GetProperty("name").GetString() ?? "").ToHashSet(StringComparer.Ordinal);
    }
}

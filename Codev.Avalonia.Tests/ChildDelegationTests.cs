using System.Diagnostics;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Codev.Avalonia.ViewModels;

namespace Codev.Avalonia.Tests;

public sealed class ChildDelegationTests
{
    [AvaloniaFact]
    public async Task Current_root_generation_can_create_an_isolated_child_while_manual_creation_stays_blocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-child-delegation-tests", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "app-data");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        var viewModel = new MainViewModel(appData);
        Codev.Conversation? child = null;
        try
        {
            await InitializeRepositoryAsync(repository);
            var parent = Assert.IsType<Codev.Conversation>(viewModel.ActiveConversation);
            viewModel.SetProjectFolder(repository);
            await viewModel.TrustProjectFolderAsync(repository);
            parent.AgentProfileName = "Orchestrator";
            await viewModel.SetProjectCommandPermissionModeAsync(Codev.ProjectCommandPermissionMode.Auto);
            var permissionField = typeof(MainViewModel).GetField("_projectCommandPermissions", BindingFlags.Instance | BindingFlags.NonPublic);
            var permissions = Assert.IsType<Codev.ProjectCommandPermissionRegistry>(permissionField?.GetValue(viewModel));
            var mcpPermissionField = typeof(MainViewModel).GetField("_projectMcpPermissions", BindingFlags.Instance | BindingFlags.NonPublic);
            var mcpPermissions = Assert.IsType<Codev.ProjectMcpToolPermissionRegistry>(mcpPermissionField?.GetValue(viewModel));
            const string deniedCommand = "Remove-Item protected.txt";
            await permissions.SetRuleAsync(repository, deniedCommand, Codev.ProjectCommandPermissionDecision.Deny);
            await permissions.SetRuleAsync(repository, "dotnet test", Codev.ProjectCommandPermissionDecision.Allow);
            using var mcpSchema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
            var deniedMcpTool = new Codev.McpCodeTaskTool("mcp_github_delete_repo", "github", "GitHub", "delete_repo",
                "Delete a GitHub repository.", mcpSchema.RootElement.Clone(), null,
                ConfigurationFingerprint: new string('a', 64));
            await mcpPermissions.SetRuleAsync(repository, deniedMcpTool.ServerId, deniedMcpTool.ToolName,
                Codev.ProjectCommandPermissionDecision.Deny, deniedMcpTool.PermissionFingerprint);

            var generationField = typeof(MainViewModel).GetField("_generationConversation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(generationField);
            generationField!.SetValue(viewModel, parent);

            var createChild = typeof(MainViewModel).GetMethod("CreateIsolatedChildSessionCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(createChild);
            var childTask = Assert.IsAssignableFrom<Task>(createChild!.Invoke(viewModel,
                [parent, false, true]));
            await childTask;
            var creation = childTask.GetType().GetProperty("Result")?.GetValue(childTask);
            child = creation?.GetType().GetProperty("Child")?.GetValue(creation) as Codev.Conversation;

            Assert.NotNull(child);
            Assert.Equal(parent.Id, child!.ParentConversationId);
            Assert.True(Directory.Exists(child.ProjectPath));
            Assert.True(new Codev.GitChildWorktreeManager(appData).IsManagedWorktreePath(child.ProjectPath!));
            Assert.Equal(Codev.ProjectCommandPermissionMode.Auto, permissions.GetMode(child.ProjectPath!));
            Assert.Contains(permissions.GetRules(child.ProjectPath!), rule =>
                rule.Command == deniedCommand && rule.Decision == Codev.ProjectCommandPermissionDecision.Deny);
            Assert.Contains(permissions.GetRules(child.ProjectPath!), rule =>
                rule.Command == "dotnet test" && rule.Decision == Codev.ProjectCommandPermissionDecision.Allow);
            Assert.Equal(Codev.ProjectCommandPermissionDecision.Deny, permissions.Evaluate(child.ProjectPath!, deniedCommand));
            Assert.Equal(Codev.ProjectCommandPermissionDecision.Allow, permissions.Evaluate(child.ProjectPath!, "dotnet build"));
            Assert.Equal(Codev.ProjectCommandPermissionDecision.Deny,
                mcpPermissions.Evaluate(child.ProjectPath!, Codev.ProjectCommandPermissionMode.Auto,
                    deniedMcpTool.ServerId, deniedMcpTool.ToolName, deniedMcpTool.PermissionFingerprint));
            var serverCalls = 0;
            var childExecutor = new Codev.CodeTaskToolExecutor(new Codev.WorkspaceFileService(child.ProjectPath!), new Codev.Conversation(),
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                mcpTools: new Dictionary<string, Codev.McpCodeTaskTool>(StringComparer.Ordinal) { [deniedMcpTool.FunctionName] = deniedMcpTool },
                mcpPermissionApproval: (tool, _, _) =>
                {
                    var decision = mcpPermissions.Evaluate(child.ProjectPath!, permissions.GetMode(child.ProjectPath!),
                        tool.ServerId, tool.ToolName, tool.PermissionFingerprint);
                    return Task.FromResult(decision switch
                    {
                        Codev.ProjectCommandPermissionDecision.Allow => Codev.CommandApprovalOutcome.Approved,
                        Codev.ProjectCommandPermissionDecision.Deny => Codev.CommandApprovalOutcome.Denied,
                        _ => Codev.CommandApprovalOutcome.Rejected
                    });
                },
                mcpCall: (_, _, _) =>
                {
                    serverCalls++;
                    return Task.FromResult("unsafe result");
                });
            using var mcpArguments = System.Text.Json.JsonDocument.Parse("{}");
            var deniedMcpResult = await childExecutor.ExecuteAsync(deniedMcpTool.FunctionName, mcpArguments.RootElement);
            Assert.Contains("Denied by a saved project MCP tool permission rule", deniedMcpResult, StringComparison.Ordinal);
            Assert.Equal(0, serverCalls);
            Assert.False((await new Codev.GitRepositoryService(repository).GetStatusAsync()).HasChanges);
            Assert.Contains("Shell commands still run with your account permissions and are not sandboxed", viewModel.ContextActionStatus, StringComparison.Ordinal);
            Assert.False(await viewModel.CreateIsolatedChildSessionAsync(parent));
        }
        finally
        {
            await viewModel.StopBackgroundCommandsAndShutdownAsync();
            if (child?.ProjectPath is { } childPath && Directory.Exists(repository))
                await RunGitAsync(repository, "worktree", "remove", "--force", childPath);
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task Child_worktree_is_not_trusted_when_inherited_mcp_permissions_cannot_be_copied()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-child-permission-order-tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "app-data");
        var codevData = Path.Combine(dataRoot, "Codev");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(codevData);
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(codevData, "avalonia-mcp-permissions.json"), "{ invalid json");
        MainViewModel? viewModel = null;
        Codev.Conversation? child = null;
        try
        {
            await InitializeRepositoryAsync(repository);
            viewModel = new MainViewModel(dataRoot);
            Assert.False(viewModel.CanPersistMcpToolPermissions);
            var parent = Assert.IsType<Codev.Conversation>(viewModel.ActiveConversation);
            viewModel.SetProjectFolder(repository);
            await viewModel.TrustProjectFolderAsync(repository);
            parent.AgentProfileName = "Orchestrator";
            await viewModel.SetProjectCommandPermissionModeAsync(Codev.ProjectCommandPermissionMode.Auto);

            var createChild = typeof(MainViewModel).GetMethod("CreateIsolatedChildSessionCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(createChild);
            var childTask = Assert.IsAssignableFrom<Task>(createChild!.Invoke(viewModel, [parent, false, false]));
            await childTask;
            Assert.Null(childTask.GetType().GetProperty("Result")?.GetValue(childTask));

            var conversationsField = typeof(MainViewModel).GetField("_conversations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(conversationsField);
            var conversations = Assert.IsAssignableFrom<IEnumerable<Codev.Conversation>>(conversationsField!.GetValue(viewModel));
            child = Assert.Single(conversations, conversation => conversation.ParentConversationId == parent.Id);
            Assert.True(Directory.Exists(child.ProjectPath));
            Assert.True(new Codev.GitChildWorktreeManager(dataRoot).IsManagedWorktreePath(child.ProjectPath!));
            Assert.False(child.IsCodeTask);
            Assert.False(Codev.ProjectFolderTrustRegistry.Load(Path.Combine(codevData, "avalonia-trusted-folders.json"))
                .IsTrusted(child.ProjectPath!));
            Assert.Contains("setup stopped before all trust, shell, and MCP deny rules were copied", viewModel.ContextActionStatus,
                StringComparison.Ordinal);
        }
        finally
        {
            if (viewModel is not null) await viewModel.StopBackgroundCommandsAndShutdownAsync();
            if (child?.ProjectPath is { } childPath && Directory.Exists(repository))
                await RunGitAsync(repository, "worktree", "remove", "--force", childPath);
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [LiveOllamaFact]
    public async Task Live_orchestrator_starts_two_read_only_children_in_parallel_worktrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-live-delegation-tests", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "app-data");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        MainViewModel? viewModel = null;
        Codev.Conversation? parent = null;
        var knownChildren = new Dictionary<Guid, Codev.Conversation>();
        var maxRunningChildren = 0;
        try
        {
            await InitializeRepositoryAsync(repository);
            await File.WriteAllTextAsync(Path.Combine(repository, "alpha.txt"), "alpha-value-19\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "beta.txt"), "beta-value-27\n");
            await RunGitAsync(repository, "add", "--", "alpha.txt", "beta.txt");
            await RunGitAsync(repository, "commit", "-m", "add read-only delegation fixtures");

            viewModel = new MainViewModel(appData);
            parent = Assert.IsType<Codev.Conversation>(viewModel.ActiveConversation);
            viewModel.SetProjectFolder(repository);
            await viewModel.TrustProjectFolderAsync(repository);
            await viewModel.SetProjectCommandPermissionModeAsync(Codev.ProjectCommandPermissionMode.Auto);
            parent.Model = Environment.GetEnvironmentVariable("CODEV_OLLAMA_MODEL") ?? "qwen3.6:35b-a3b";
            parent.Provider = "ollama";
            parent.IsCodeTask = true;
            parent.IsPlanMode = false;
            parent.AgentProfileName = "Orchestrator";
            parent.NumCtx = 8192;
            parent.NumPredict = 2048;
            parent.ThinkEnabled = false;
            parent.Temperature = 0;

            viewModel.Draft = "Use exactly two independent delegate_task calls with the Ask profile. The first child must read alpha.txt and report its exact value. The second child must read beta.txt and report its exact value. Do not read or edit files yourself, do not run shell commands, and do not create a checklist. Start both children and then briefly say they were delegated.";
            var send = typeof(MainViewModel).GetMethod("SendDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(send);
            var sendTask = Assert.IsAssignableFrom<Task>(send!.Invoke(viewModel, null));
            await sendTask;

            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            var sawGeneration = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                sawGeneration |= viewModel.IsGenerating;
                foreach (var child in parent.ChildConversations) knownChildren[child.Id] = child;
                maxRunningChildren = Math.Max(maxRunningChildren, parent.ChildConversations.Count(child => child.IsChildTaskRunning));
                if (sawGeneration && knownChildren.Count >= 2 && !viewModel.IsGenerating &&
                    knownChildren.Values.All(child => !child.IsChildTaskRunning)) break;
                await Task.Delay(100);
            }

            var output = string.Join("\n", parent.Messages.Select(message => message.Content));
            Assert.True(sawGeneration && !viewModel.IsGenerating, $"The parent response did not finish. Transcript: {output}");
            Assert.True(knownChildren.Count >= 2, $"The Orchestrator did not start two children. Transcript: {output}");
            Assert.All(knownChildren.Values, child => Assert.False(child.IsChildTaskRunning,
                $"Child task '{child.Title}' did not finish before the live-test deadline."));
            Assert.True(maxRunningChildren >= 2, $"The children did not overlap. Transcript: {output}");
            Assert.Equal(knownChildren.Count, knownChildren.Values.Select(child => child.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(knownChildren.Values, child =>
            {
                Assert.Equal(parent.Id, child.ParentConversationId);
                Assert.Equal("Ask", child.AgentProfileName);
                Assert.True(new Codev.GitChildWorktreeManager(appData).IsManagedWorktreePath(child.ProjectPath!));
                Assert.Equal("alpha-value-19\n", Normalize(File.ReadAllText(Path.Combine(child.ProjectPath!, "alpha.txt"))));
                Assert.Equal("beta-value-27\n", Normalize(File.ReadAllText(Path.Combine(child.ProjectPath!, "beta.txt"))));
                var childOutput = string.Join("\n", child.Messages.Select(message => message.Content));
                if (child.Title.Contains("alpha.txt", StringComparison.OrdinalIgnoreCase))
                    Assert.Contains("alpha-value-19", childOutput, StringComparison.Ordinal);
                else if (child.Title.Contains("beta.txt", StringComparison.OrdinalIgnoreCase))
                    Assert.True(childOutput.Contains("beta-value-27", StringComparison.Ordinal),
                        $"The beta child did not report the expected file value. Title: {child.Title}. Transcript: {childOutput}");
                else
                    Assert.Fail($"Unexpected delegated task title: {child.Title}");
            });
            Assert.Contains(parent.Messages.Select(message => message.Content),
                content => content.Contains("Delegated result", StringComparison.Ordinal));
            Assert.False((await new Codev.GitRepositoryService(repository).GetStatusAsync()).HasChanges);
        }
        finally
        {
            if (viewModel is not null)
            {
                if (viewModel.IsGenerating) viewModel.StopGenerationCommand.Execute(null);
                var stopDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (viewModel.IsGenerating && DateTimeOffset.UtcNow < stopDeadline) await Task.Delay(100);
                await viewModel.StopBackgroundCommandsAndShutdownAsync();
            }
            if (parent is not null)
                foreach (var child in parent.ChildConversations.Concat(knownChildren.Values).DistinctBy(child => child.Id))
                    if (child.ProjectPath is { } childPath && Directory.Exists(repository))
                        try { await RunGitAsync(repository, "worktree", "remove", "--force", childPath); } catch { }
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task InitializeRepositoryAsync(string repository)
    {
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.name", "Codev Tests");
        await RunGitAsync(repository, "config", "user.email", "codev-tests@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "committed\n");
        await RunGitAsync(repository, "add", "--", "tracked.txt");
        await RunGitAsync(repository, "commit", "-m", "initial");
    }

    private static async Task RunGitAsync(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git for the test repository.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git {string.Join(' ', arguments)} failed: {await stderr}; {await stdout}");
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}

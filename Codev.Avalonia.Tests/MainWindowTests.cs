using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codev;
using Codev.Avalonia.ViewModels;
using Codev.Avalonia.Views;
using System.Reflection;
using System.Text.Json;

namespace Codev.Avalonia.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Attached_project_exposes_a_persistable_auto_command_mode_selector()
    {
        var window = new MainWindow
        {
            DataContext = new TestProjectPermissionViewModel(
                HasProject: true,
                CanPersistProjectCommandPermissions: true,
                ProjectCommandPermissionModeLabel: "Auto ▾")
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var selector = Assert.IsType<Button>(window.FindControl<Button>("ProjectCommandModeButton"));
            Assert.True(selector.IsVisible);
            Assert.True(selector.IsEnabled);
            Assert.Equal("Auto ▾", selector.Content?.ToString());
            var menu = Assert.IsType<MenuFlyout>(selector.Flyout);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Auto · approve unless denied");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Footer_auto_default_is_visible_without_project_and_inherited_by_new_code_task_workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var appData = Path.Combine(root, "Codev");
        Directory.CreateDirectory(appData);
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-settings.json"),
            AvaloniaUiSettings.Serialize(AvaloniaUiSettings.Default with { DefaultProjectCommandPermissionMode = ProjectCommandPermissionMode.Auto }));
        var viewModel = new MainViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            var selector = Assert.IsType<Button>(window.FindControl<Button>("ProjectCommandModeButton"));
            Assert.True(selector.IsVisible);
            Assert.Equal("Auto ▾", selector.Content?.ToString());
            var menu = Assert.IsType<MenuFlyout>(selector.Flyout);
            menu.ShowAt(selector);
            var askItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Ask every time");
            await Dispatcher.UIThread.InvokeAsync(() => askItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            var settingsPath = Path.Combine(appData, "avalonia-settings.json");
            var settingsDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < settingsDeadline)
            {
                if (File.Exists(settingsPath) && AvaloniaUiSettings.Deserialize(await File.ReadAllTextAsync(settingsPath)).DefaultProjectCommandPermissionMode == ProjectCommandPermissionMode.AskEveryTime) break;
                await Task.Delay(20);
            }
            Assert.True(File.Exists(settingsPath));
            Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, AvaloniaUiSettings.Deserialize(await File.ReadAllTextAsync(settingsPath)).DefaultProjectCommandPermissionMode);

            var conversation = viewModel.ActiveConversation!;
            var enableCodeTask = typeof(MainViewModel).GetMethod("EnableCodeTaskWithWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Assert.IsAssignableFrom<Task>(enableCodeTask.Invoke(viewModel, [conversation]));
            Assert.NotNull(conversation.ProjectPath);
            Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, viewModel.ProjectCommandPermissionMode);
            var registryPath = Path.Combine(appData, "avalonia-command-permissions.json");
            Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, ProjectCommandPermissionRegistry.Load(registryPath).GetMode(conversation.ProjectPath!));
        }
        finally
        {
            window.Close();
            await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Footer_auto_selection_persists_and_skips_inline_command_approval()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Codev");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(project);
        var settings = AvaloniaUiSettings.Default with { DefaultProjectCommandPermissionMode = ProjectCommandPermissionMode.AskEveryTime };
        var settingsPath = Path.Combine(appData, "avalonia-settings.json");
        await File.WriteAllTextAsync(settingsPath, AvaloniaUiSettings.Serialize(settings));
        MainViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            viewModel = new MainViewModel(root);
            viewModel.SetProjectFolder(project);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var selector = Assert.IsType<Button>(window.FindControl<Button>("ProjectCommandModeButton"));
            Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, viewModel.ProjectCommandPermissionMode);
            Assert.Equal("Ask every time ▾", selector.Content?.ToString());
            var menu = Assert.IsType<MenuFlyout>(selector.Flyout);
            menu.ShowAt(selector);
            var autoItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Auto · approve unless denied");
            await Dispatcher.UIThread.InvokeAsync(() => autoItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));

            var modeDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while ((viewModel.ProjectCommandPermissionMode != ProjectCommandPermissionMode.Auto || selector.Content?.ToString() != "Auto ▾") &&
                   DateTimeOffset.UtcNow < modeDeadline)
                await Task.Delay(20);
            Assert.Equal(ProjectCommandPermissionMode.Auto, viewModel.ProjectCommandPermissionMode);
            Assert.Equal("Auto ▾", selector.Content?.ToString());
            var permissionPath = (string)typeof(MainViewModel).GetField("ProjectCommandPermissionsPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
            var permissionDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (ProjectCommandPermissionRegistry.Load(permissionPath).GetMode(project) != ProjectCommandPermissionMode.Auto &&
                   DateTimeOffset.UtcNow < permissionDeadline)
                await Task.Delay(20);
            Assert.Equal(ProjectCommandPermissionMode.Auto, ProjectCommandPermissionRegistry.Load(permissionPath).GetMode(project));

            var settingsDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < settingsDeadline)
            {
                if (File.Exists(settingsPath) &&
                    AvaloniaUiSettings.Deserialize(await File.ReadAllTextAsync(settingsPath)).DefaultProjectCommandPermissionMode == ProjectCommandPermissionMode.Auto) break;
                await Task.Delay(20);
            }
            var savedDefault = AvaloniaUiSettings.Deserialize(await File.ReadAllTextAsync(settingsPath)).DefaultProjectCommandPermissionMode;
            Assert.Equal(ProjectCommandPermissionMode.Auto, savedDefault);

            var approvePolicy = typeof(MainViewModel).GetMethod("ApproveCommandWithProjectPolicyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var proposal = new CodeTaskCommandProposal("cargo check --manifest-path signaling/Cargo.toml", project, "PowerShell", IsVerification: true);
            var pendingApproval = Assert.IsAssignableFrom<Task<CommandApprovalOutcome>>(approvePolicy.Invoke(viewModel, [proposal, Array.Empty<string>()]));
            Assert.Equal(CommandApprovalOutcome.Approved, await pendingApproval);
            Assert.False(Assert.IsType<Border>(window.FindControl<Border>("InlineApprovalPanel")).IsVisible);

            window.Close();
            window = null;
            await StopAndFlushAsync(viewModel);
            viewModel = null;

            var restored = new MainViewModel(root);
            try
            {
                Assert.Equal(project, restored.ActiveConversation?.ProjectPath);
                Assert.Equal(ProjectCommandPermissionMode.Auto, restored.ProjectCommandPermissionMode);
            }
            finally { await StopAndFlushAsync(restored); }

        }
        finally
        {
            window?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    private static async Task StopAndFlushAsync(MainViewModel viewModel)
    {
        var startup = typeof(MainViewModel).GetField("_managedWorkspacePermissionDefaultsTask", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel) as Task;
        if (startup is not null) await startup.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.SavePendingDraftAsync();
        await viewModel.StopBackgroundCommandsAndShutdownAsync();
        var persistence = typeof(MainViewModel).GetField("_persistenceTask", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel) as Task;
        if (persistence is not null) await persistence.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task DeleteAutoModeTestDirectoryAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var expectedParent = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui") + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(Path.GetFileName(fullPath), out _))
            throw new InvalidOperationException($"Refusing to remove a path outside the isolated Auto-mode test directory: {fullPath}");

        for (var attempt = 0; attempt < 20 && Directory.Exists(fullPath); attempt++)
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(50);
            }
        }
        if (Directory.Exists(fullPath)) throw new IOException($"Could not clean up the isolated Auto-mode test directory: {fullPath}");
    }

    [AvaloniaFact]
    public async Task Project_formatter_menu_is_available_only_for_trusted_projects()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-project-formatter-ui", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var viewModel = new MainViewModel(Path.Combine(root, "app-data"));
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            viewModel.SetProjectFolder(project);
            window.Show();
            window.UpdateLayout();
            var actions = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Project actions…");
            var menu = Assert.IsType<MenuFlyout>(actions.Flyout);
            menu.ShowAt(actions);
            window.UpdateLayout();
            var formatterItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Project formatters…");
            Assert.False(formatterItem.IsEnabled);

            await viewModel.TrustProjectFolderAsync(project);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(formatterItem.IsEnabled);

            await viewModel.ToggleProjectFolderTrustAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.False(formatterItem.IsEnabled);
        }
        finally
        {
            window.Close();
            await viewModel.StopBackgroundCommandsAndShutdownAsync();
            var persistence = typeof(MainViewModel).GetField("_persistenceTask", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel) as Task;
            if (persistence is not null) await persistence.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Main_window_loads_and_composer_accepts_keyboard_input()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var composer = Assert.IsType<TextBox>(window.FindControl<TextBox>("ComposerTextBox"));
            composer.Focus();

            window.KeyTextInput("Headless UI smoke");

            Assert.Equal("Headless UI smoke", composer.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tool_output_details_start_collapsed()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var message = new ChatMessage("assistant", "**run command**\n" + UntrustedToolOutput.Format("approved command output", "Exit code: 0", command: "dotnet test"));
            var messages = Assert.IsType<ItemsControl>(window.FindControl<ItemsControl>("MessageList"));
            messages.ItemsSource = new[] { message };
            window.UpdateLayout();
            var details = Assert.Single(window.GetVisualDescendants().OfType<Expander>(),
                expander => expander.Header?.ToString() == "Ran commands");

            Assert.False(details.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Restored_queued_turn_explains_that_resume_is_required()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-restored-queue-ui", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Codev");
        Directory.CreateDirectory(appData);
        var conversation = new Conversation
        {
            Title = "Queued local request",
            Model = "qwen3.6:35b-a3b",
            Provider = "ollama",
            Messages =
            [
                new ChatMessage("user", "tell me a joke"),
                new ChatMessage("assistant", "Queued locally · waiting for the current response")
            ],
            PendingTurns =
            [
                new PersistedQueuedTurn(1, "qwen3.6:35b-a3b", 0, false, false, null, [], [], DateTimeOffset.Now)
            ]
        };
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-conversations.json"),
            JsonSerializer.Serialize(new[] { conversation }));

        var viewModel = new MainViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.True(viewModel.IsQueuePaused);
            Assert.Equal("Saved locally · select Resume saved queue to run", viewModel.Messages[1].Content);
            Assert.Contains("Resume to continue", viewModel.QueueStatusLabel, StringComparison.Ordinal);
            var resume = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.Content?.ToString() == "Resume saved queue");
            Assert.True(resume.IsVisible);
            Assert.True(resume.IsEnabled);

            viewModel.Draft = "A follow-up must stay behind this conversation's saved turn.";
            viewModel.SendCommand.Execute(null);
            Assert.False(viewModel.IsGenerating);
            Assert.Equal("Saved locally · select Resume saved queue to run", viewModel.Messages[3].Content);
            Assert.Equal(2, viewModel.ActiveConversation!.PendingTurns.Count);
        }
        finally
        {
            window.Close();
            await viewModel.StopBackgroundCommandsAndShutdownAsync();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await viewModel.SavePendingDraftAsync();
                if (!Directory.EnumerateFiles(appData, "*.tmp").Any()) break;
                await Task.Delay(20);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Background_command_status_panel_starts_collapsed()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            var expander = Assert.IsType<Expander>(window.FindControl<Expander>("BackgroundCommandsExpander"));
            Assert.False(expander.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Expanded_background_command_panel_shows_elapsed_time_and_stop_action()
    {
        var command = new BackgroundCommandSnapshot("ab12cd34ef56", Guid.NewGuid(), "npm run dev", Path.GetTempPath(),
            "Running", DateTimeOffset.UtcNow.AddSeconds(-74), TimeSpan.FromSeconds(74), "", null);
        var window = new MainWindow { DataContext = new TestBackgroundCommandViewModel(command) };
        try
        {
            window.Show();
            var expander = Assert.IsType<Expander>(window.FindControl<Expander>("BackgroundCommandsExpander"));
            expander.IsExpanded = true;
            window.UpdateLayout();

            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("01:14", StringComparison.Ordinal) == true);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("npm run dev", StringComparison.Ordinal) == true);
            var stop = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Stop");
            Assert.True(stop.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Verification_approval_is_rendered_inline_and_run_once_resolves_the_request()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var proposal = new CodeTaskCommandProposal("node --check game.js", Path.GetTempPath(), "PowerShell", IsVerification: true);
            var request = typeof(MainWindow).GetMethod("ApproveAgentCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = Assert.IsAssignableFrom<Task<ProjectCommandApprovalChoice>>(request.Invoke(window, [proposal]));

            await Dispatcher.UIThread.InvokeAsync(() => { });

            var panel = Assert.IsType<Border>(window.FindControl<Border>("InlineApprovalPanel"));
            Assert.True(panel.IsVisible);
            var content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            var command = Assert.Single(content.GetVisualDescendants().OfType<TextBox>(), box => box.Text == proposal.Command);
            Assert.True(command.IsReadOnly);
            AssertReadableContrast(command);
            var runOnce = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Run once & verify");

            await Dispatcher.UIThread.InvokeAsync(() => runOnce.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Equal(ProjectCommandApprovalChoice.RunOnce, await pending);
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Background_command_review_shows_lifetime_warning_and_exact_command()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var proposal = new CodeTaskCommandProposal("npm run dev", Path.GetTempPath(), "PowerShell", IsBackground: true);
            var request = typeof(MainWindow).GetMethod("ApproveAgentCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = Assert.IsAssignableFrom<Task<ProjectCommandApprovalChoice>>(request.Invoke(window, [proposal]));
            await Dispatcher.UIThread.InvokeAsync(() => { });

            var content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            Assert.Contains(content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("may open a network port", StringComparison.Ordinal) == true);
            Assert.Contains(content.GetVisualDescendants().OfType<TextBox>(), box => box.Text == proposal.Command && box.IsReadOnly);
            var runOnce = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Run once");
            await Dispatcher.UIThread.InvokeAsync(() => runOnce.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.Equal(ProjectCommandApprovalChoice.RunOnce, await pending);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Mcp_approval_is_rendered_inline_and_run_once_resolves_the_request()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""");
            using var arguments = JsonDocument.Parse("""{"query":"codev"}""");
            var tool = new McpCodeTaskTool("mcp_github_search", "github", "GitHub", "search", "Search repositories.", schema.RootElement.Clone(), null);
            var request = typeof(MainWindow).GetMethod("ApproveAgentMcpToolAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = Assert.IsAssignableFrom<Task<ProjectCommandApprovalChoice>>(request.Invoke(window, [tool, arguments.RootElement]));

            await Dispatcher.UIThread.InvokeAsync(() => { });

            var panel = Assert.IsType<Border>(window.FindControl<Border>("InlineApprovalPanel"));
            Assert.True(panel.IsVisible);
            var content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            Assert.Contains(content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("GitHub · search", StringComparison.Ordinal) == true);
            var argumentsBox = Assert.Single(content.GetVisualDescendants().OfType<TextBox>(), box => box.Text == arguments.RootElement.GetRawText());
            AssertReadableContrast(argumentsBox);
            var runOnce = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Run once");

            await Dispatcher.UIThread.InvokeAsync(() => runOnce.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Equal(ProjectCommandApprovalChoice.RunOnce, await pending);
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Agent_profile_one_call_approval_is_inline_and_allow_once_resolves_the_request()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var profile = new AgentProfile("Review", "Review before tool calls.", null, null, null,
                AgentToolPermission.Ask, new Dictionary<string, AgentToolPermission>(), "", "user", "review.md");
            using var arguments = JsonDocument.Parse("""{"path":"README.md"}""");
            var request = typeof(MainWindow).GetMethod("ConfirmAgentProfileToolAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = Assert.IsAssignableFrom<Task<bool>>(request.Invoke(window, [profile, "read_file", arguments.RootElement]));

            await Dispatcher.UIThread.InvokeAsync(() => { });

            var panel = Assert.IsType<Border>(window.FindControl<Border>("InlineApprovalPanel"));
            Assert.True(panel.IsVisible);
            var content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            Assert.Contains(content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("Review asks before read file", StringComparison.Ordinal) == true);
            var argumentsBox = Assert.Single(content.GetLogicalDescendants().OfType<TextBox>(), box => box.Text == arguments.RootElement.GetRawText());
            AssertReadableContrast(argumentsBox);
            var allow = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Allow once");

            await Dispatcher.UIThread.InvokeAsync(() => allow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.True(await pending);
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Agent_profile_editor_validates_then_saves_and_reloads_user_profile()
    {
        // The profile store rejects symlinked ancestors. Use a disposable
        // directory under the test output path instead of the OS temp path,
        // which can contain symlinked ancestors on macOS.
        var root = Path.Combine(AppContext.BaseDirectory, "Codev-agent-profile-editor-ui", Guid.NewGuid().ToString("N"));
        var profileStore = new UserAgentProfileStore(root);
        var profileService = new TestAgentProfileEditorService(profileStore);
        var editor = new AgentProfileEditorWindow(profileService, await profileService.GetUserAgentProfileDocumentsAsync());
        try
        {
            editor.Show();
            editor.UpdateLayout();
            var fileName = Assert.Single(editor.GetVisualDescendants().OfType<TextBox>(), control => control.Name == "AgentProfileFileNameTextBox");
            var contents = Assert.Single(editor.GetVisualDescendants().OfType<TextBox>(), control => control.Name == "AgentProfileContentsTextBox");
            var status = Assert.Single(editor.GetVisualDescendants().OfType<TextBlock>(), control => control.Name == "AgentProfileEditorStatus");
            var create = Assert.Single(editor.GetVisualDescendants().OfType<Button>(), control => control.Name == "NewProfileButton");
            var save = Assert.Single(editor.GetVisualDescendants().OfType<Button>(), control => control.Name == "SaveAgentProfileButton");

            await Dispatcher.UIThread.InvokeAsync(() => create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            fileName.Text = "reviewer";
            contents.Text = "This is not a valid profile.";
            await Dispatcher.UIThread.InvokeAsync(() => save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Assert.ThrowsAsync<InvalidDataException>(() => profileService.LastSaveTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitForStatusAsync(status, "Profile was not saved");

            Assert.False(File.Exists(Path.Combine(root, "reviewer.md")));
            Assert.Contains("Profile was not saved", status.Text, StringComparison.Ordinal);

            contents.Text = "---\nname: Reviewer\ndescription: Review source changes carefully.\ndefault_permission: ask\ntools: read_file=allow, search_files=allow\n---\nInspect the relevant code before suggesting edits.\n";
            await Dispatcher.UIThread.InvokeAsync(() => save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await profileService.LastSaveTask.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForStatusAsync(status, "Profile saved and available");

            var saved = await profileService.GetUserAgentProfileDocumentsAsync();
            Assert.Equal("reviewer.md", Assert.Single(saved).FileName);
            Assert.Contains("name: Reviewer", saved[0].Contents, StringComparison.Ordinal);
            Assert.Contains("Profile saved and available in the Code task profile picker", status.Text, StringComparison.Ordinal);
            var selection = Assert.Single(editor.GetVisualDescendants().OfType<ComboBox>(), control => control.Name == "AgentProfileSelectionComboBox");
            Assert.Contains("reviewer.md", selection.ItemsSource!.Cast<string>());
        }
        finally
        {
            editor.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Code_task_profile_picker_selects_a_saved_profile_on_the_active_conversation()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Codev-agent-profile-picker-ui", Guid.NewGuid().ToString("N"));
        var store = new UserAgentProfileStore(Path.Combine(root, "Codev", "agents"));
        await store.SaveAsync("reviewer.md", "---\nname: Reviewer\ndescription: Review source changes.\n---\nInspect first, then report clearly.\n");
        var viewModel = new MainViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            await viewModel.RefreshAgentProfilesAsync();
            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            typeof(MainViewModel).GetMethod("SetConversationMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [ConversationMode.CodeTask]);
            window.Show();
            window.UpdateLayout();

            var picker = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("AgentProfileSelectionComboBox"));
            Assert.True(picker.IsEnabled);
            Assert.Contains(viewModel.AgentProfiles, profile => profile.Name == "Reviewer");
            var opencodeDirectory = Path.Combine(root, ".opencode", "agents");
            Directory.CreateDirectory(opencodeDirectory);
            await File.WriteAllTextAsync(Path.Combine(opencodeDirectory, "subtask.md"), "---\ndescription: Handle delegated work.\nmode: subagent\n---\nWork only on the bounded delegated task.");
            await viewModel.RefreshAgentProfilesAsync();
            Assert.DoesNotContain(viewModel.AgentProfiles, profile => profile.Name == "subtask");
            picker.SelectedValue = "Reviewer";
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.Equal("Reviewer", viewModel.SelectedAgentProfileName);
            Assert.Equal("Reviewer", conversation.AgentProfileName);
            Assert.Equal("Reviewer agent", viewModel.PrimaryAgentLabel);

            picker.SelectedValue = "Plan";
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("Plan", conversation.AgentProfileName);
            Assert.Equal("Plan agent", viewModel.PrimaryAgentLabel);

            picker.SelectedValue = "";
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Null(conversation.AgentProfileName);
            Assert.Equal("Build agent", viewModel.PrimaryAgentLabel);

            window.KeyPress(Key.A, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.A, "A");
            Assert.Equal("Plan", conversation.AgentProfileName);
            Assert.Equal("Plan agent", viewModel.PrimaryAgentLabel);

            window.KeyPress(Key.A, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.A, "A");
            Assert.Null(conversation.AgentProfileName);
            Assert.Equal("Build agent", viewModel.PrimaryAgentLabel);
        }
        finally
        {
            window.Close();
            await viewModel.StopBackgroundCommandsAndShutdownAsync();
        }
    }

    [AvaloniaFact]
    public void File_review_is_readable_collapsed_by_default_and_keeps_decisions_visible()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var buildReview = typeof(MainWindow).GetMethod("BuildFileApprovalContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var review = Assert.IsAssignableFrom<Control>(buildReview.Invoke(window,
                ["index.html", "", "<h1>Updated</h1>", true, null, null]));
            var content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            content.Content = review;
            window.UpdateLayout();

            var details = Assert.Single(review.GetVisualDescendants().OfType<Expander>(), expander => expander.Header?.ToString() == "Show current and proposed files");
            Assert.False(details.IsExpanded);
            details.IsExpanded = true;
            window.UpdateLayout();
            var panes = review.GetLogicalDescendants().OfType<TextBox>().ToArray();
            Assert.Equal(2, panes.Length);
            Assert.All(panes, AssertReadableContrast);
            Assert.Contains(review.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Keep unchanged");
            Assert.Contains(review.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Approve & create");
        }
        finally
        {
            window.Close();
        }
    }

    private sealed record TestProjectPermissionViewModel(bool HasProject,
        bool CanPersistProjectCommandPermissions, string ProjectCommandPermissionModeLabel);

    private sealed class TestBackgroundCommandViewModel(BackgroundCommandSnapshot command)
    {
        public System.Collections.ObjectModel.ObservableCollection<BackgroundCommandSnapshot> BackgroundCommands { get; } = [command];
        public bool HasBackgroundCommands => true;
        public string BackgroundCommandsHeader => "Background commands · 1";
        public System.Windows.Input.ICommand StopBackgroundCommand { get; } = new NoopCommand();
    }

    private sealed class NoopCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    private sealed class TestAgentProfileEditorService(UserAgentProfileStore store) : Codev.Avalonia.ViewModels.IUserAgentProfileEditorService
    {
        public Task LastSaveTask { get; private set; } = Task.CompletedTask;

        public Task<IReadOnlyList<AgentProfileDocument>> GetUserAgentProfileDocumentsAsync(CancellationToken cancellationToken = default) =>
            store.LoadDocumentsAsync(cancellationToken);

        public async Task SaveUserAgentProfileAsync(string fileName, string contents, CancellationToken cancellationToken = default)
        {
            LastSaveTask = store.SaveAsync(fileName, contents, cancellationToken);
            await LastSaveTask;
        }
    }

    private static void AssertReadableContrast(TextBox textBox)
    {
        var foreground = Assert.IsType<SolidColorBrush>(textBox.Foreground).Color;
        var background = Assert.IsType<SolidColorBrush>(textBox.Background).Color;
        Assert.True(ContrastRatio(foreground, background) >= 4.5,
            $"Expected readable text contrast, got {foreground} on {background}.");
    }

    private static async Task WaitForStatusAsync(TextBlock status, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            if (status.Text?.Contains(expected, StringComparison.Ordinal) == true) return;
            await Task.Delay(10);
        }
        Assert.Contains(expected, status.Text, StringComparison.Ordinal);
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}

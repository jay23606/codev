using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codev;
using Codev.Avalonia.ViewModels;
using Codev.Avalonia.Views;
using System.Net;
using System.Text;
using System.Reflection;
using System.Text.Json;

namespace Codev.Avalonia.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task Backup_menu_exports_and_imports_through_the_platform_picker()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var picker = new FakeConversationBackupPicker();
        MainViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            viewModel = new MainViewModel(root);
            var original = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            original.Title = "Menu backup round trip";
            original.Messages = [new("user", "Keep this conversation"), new("assistant", "Backup UI passed")];
            var initialConversationCount = viewModel.RecentConversations.Count;

            window = new MainWindow(picker) { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var button = Assert.IsType<Button>(window.FindControl<Button>("ChatBackupsButton"));
            var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
            flyout.ShowAt(button);
            var exportItem = Assert.Single(flyout.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Export all chats…");
            await Dispatcher.UIThread.InvokeAsync(() => exportItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            await WaitForContextActionStatusAsync(viewModel, "Backup saved · backup.codev.json");

            var saveOptions = Assert.IsType<FilePickerSaveOptions>(picker.LastSaveOptions);
            Assert.Contains("*.codev.json", Assert.Single(saveOptions.FileTypeChoices!).Patterns!);
            Assert.Contains("Menu backup round trip", picker.File.ReadAllText(), StringComparison.Ordinal);
            Assert.Contains("Backup UI passed", picker.File.ReadAllText(), StringComparison.Ordinal);

            flyout.ShowAt(button);
            var importItem = Assert.Single(flyout.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Import chats…");
            await Dispatcher.UIThread.InvokeAsync(() => importItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            await WaitForContextActionStatusAsync(viewModel, "Imported 1 conversation(s).");

            Assert.NotNull(picker.LastOpenOptions);
            Assert.False(picker.LastOpenOptions!.AllowMultiple);
            Assert.Equal(initialConversationCount + 1, viewModel.RecentConversations.Count);
            Assert.Contains(viewModel.RecentConversations, conversation => conversation.Id == original.Id);
            var imported = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            Assert.NotEqual(original.Id, imported.Id);
            Assert.Equal("Menu backup round trip", imported.Title);
            Assert.Equal("Backup UI passed", imported.Messages[1].Content);
        }
        finally
        {
            window?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Model_picker_shows_loading_empty_and_unavailable_states_without_blank_options()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "app-data");
        var settingsDirectory = Path.Combine(appData, "Codev");
        Directory.CreateDirectory(settingsDirectory);

        var endpoint = "http://127.0.0.1:11434";
        File.WriteAllText(Path.Combine(settingsDirectory, "avalonia-settings.json"),
            JsonSerializer.Serialize(new AvaloniaUiSettings("dark", endpoint)));

        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEmptyModelsResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var httpHandler = new TestHttpMessageHandler((request, cancellationToken) =>
        {
            Assert.EndsWith("/api/tags", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                firstRequestStarted.TrySetResult();
                return releaseEmptyModelsResponse.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        MainViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            viewModel = new MainViewModel(appData, httpHandler);
            var initialLoad = (Task)typeof(MainViewModel)
                .GetField("_modelLoadTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel)!;
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout());
            var modelPicker = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("ModelPicker"));
            var placeholder = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Loading Ollama models…");
            Assert.False(modelPicker.IsVisible);
            Assert.True(placeholder.IsVisible);

            releaseEmptyModelsResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[]}", Encoding.UTF8, "application/json")
            });
            await initialLoad.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout());

            Assert.Empty(viewModel.Models);
            Assert.False(viewModel.HasModels);
            Assert.False(modelPicker.IsVisible);
            Assert.True(placeholder.IsVisible);
            Assert.Equal("No local chat models installed", placeholder.Text);
            Assert.Contains("Ollama connected · no chat-capable models installed", viewModel.ConnectionStatus, StringComparison.Ordinal);

            Assert.True(await viewModel.SetOllamaEndpointAsync("http://127.0.0.1:11435"));
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout());

            Assert.False(viewModel.HasModels);
            Assert.False(modelPicker.IsVisible);
            Assert.True(placeholder.IsVisible);
            Assert.Equal("Ollama unavailable", placeholder.Text);
            Assert.Contains("Ollama is not reachable", viewModel.ConnectionStatus, StringComparison.Ordinal);
        }
        finally
        {
            releaseEmptyModelsResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[]}", Encoding.UTF8, "application/json")
            });
            window?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Backup_round_trip_through_view_model_keeps_existing_history_and_drops_runtime_authority()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        MainViewModel? viewModel = null;
        try
        {
            viewModel = new MainViewModel(root);
            viewModel.SetProjectFolder(project);
            var source = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            source.Title = "Backup safety smoke";
            source.Provider = CloudModelProviders.OpenAI;
            source.Model = "gpt-5.6";
            source.IsCodeTask = true;
            source.AllowHostedCodeTask = true;
            source.IncludeProjectContextForHosted = true;
            source.Messages = [new("user", "Keep this history"), new("assistant", "History retained")];
            source.PendingRequestCount = 1;
            source.PendingTurns = [new PersistedQueuedTurn(1, source.Model, 8192, true, false, project, [], [], DateTimeOffset.UtcNow)];
            source.FileChanges = [new FileChangeRecord("src/app.cs", Path.Combine(root, "checkpoint.txt"), DateTimeOffset.UtcNow, "Modified")];

            var backup = await viewModel.ExportConversationBackupAsync();
            Assert.DoesNotContain("PendingTurns", backup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("checkpoint.txt", backup, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(1, await viewModel.ImportConversationBackupAsync(backup));
            var imported = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            Assert.NotEqual(source.Id, imported.Id);
            Assert.Equal("History retained", imported.Messages[1].Content);
            Assert.False(imported.AllowHostedCodeTask);
            Assert.False(imported.IncludeProjectContextForHosted);
            Assert.False(imported.IsCodeTask);
            Assert.Empty(imported.PendingTurns);
            Assert.Equal(0, imported.PendingRequestCount);
            Assert.Null(imported.FileChanges[0].CheckpointPath);
            Assert.False(viewModel.IsProjectTrusted);

            Assert.Equal("History retained", source.Messages[1].Content);
            Assert.Single(source.PendingTurns);
            Assert.Equal(1, source.PendingRequestCount);
            await StopAndFlushAsync(viewModel);
            viewModel = null;

            var restored = new MainViewModel(root);
            try
            {
                Assert.Equal(imported.Id, restored.ActiveConversation?.Id);
                Assert.False(restored.ActiveConversation!.AllowHostedCodeTask);
                Assert.Empty(restored.ActiveConversation.PendingTurns);
                Assert.False(restored.IsProjectTrusted);
            }
            finally { await StopAndFlushAsync(restored); }
        }
        finally
        {
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Theme_button_persists_the_selected_theme_across_view_model_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        MainViewModel? firstViewModel = null;
        MainWindow? firstWindow = null;
        MainViewModel? restoredViewModel = null;
        MainWindow? restoredWindow = null;
        try
        {
            firstViewModel = new MainViewModel(root);
            firstWindow = new MainWindow { DataContext = firstViewModel };
            firstWindow.Show();
            firstWindow.UpdateLayout();

            Assert.True(firstViewModel.IsDarkTheme);
            var themeButton = Assert.Single(firstWindow.GetVisualDescendants().OfType<Button>(),
                button => button.Content?.ToString()?.Contains("Switch to light mode", StringComparison.Ordinal) == true);
            Assert.NotNull(themeButton.Command);
            themeButton.Command!.Execute(themeButton.CommandParameter);

            Assert.False(firstViewModel.IsDarkTheme);
            var settingsPath = Path.Combine(root, "Codev", "avalonia-settings.json");
            var settingsSave = typeof(MainViewModel).GetField("_settingsPersistenceTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(firstViewModel) as Task;
            Assert.NotNull(settingsSave);
            await settingsSave!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("light", AvaloniaUiSettings.Deserialize(await File.ReadAllTextAsync(settingsPath)).Theme);

            firstWindow.Close();
            firstWindow = null;
            await StopAndFlushAsync(firstViewModel);
            firstViewModel = null;

            restoredViewModel = new MainViewModel(root);
            restoredWindow = new MainWindow { DataContext = restoredViewModel };
            restoredWindow.Show();
            restoredWindow.UpdateLayout();

            Assert.False(restoredViewModel.IsDarkTheme);
            Assert.Single(restoredWindow.GetVisualDescendants().OfType<Button>(),
                button => button.Content?.ToString()?.Contains("Switch to dark mode", StringComparison.Ordinal) == true);
        }
        finally
        {
            firstWindow?.Close();
            restoredWindow?.Close();
            if (firstViewModel is not null) await StopAndFlushAsync(firstViewModel);
            if (restoredViewModel is not null) await StopAndFlushAsync(restoredViewModel);
            if (global::Avalonia.Application.Current is { } app)
                app.RequestedThemeVariant = global::Avalonia.Styling.ThemeVariant.Dark;
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task F1_opens_keyboard_shortcuts_reference_in_Avalonia()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        MainViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            viewModel = new MainViewModel(root);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.F1,
                KeyModifiers = KeyModifiers.None
            });

            var dialog = Assert.IsType<Window>(typeof(MainWindow)
                .GetField("_keyboardShortcutsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window));
            Assert.True(dialog.IsVisible);
            var lines = dialog.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text).ToArray();
            Assert.Contains(lines, line => line?.Contains("Ctrl/⌘+Shift+M", StringComparison.Ordinal) == true);
            Assert.Contains(lines, line => line?.Contains("/status", StringComparison.Ordinal) == true);
            Assert.Contains(lines, line => line?.Contains("F1  Show these shortcuts", StringComparison.Ordinal) == true);
            dialog.Close();
        }
        finally
        {
            window?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Conversation_mode_shortcut_respects_generation_fallback_and_persists_per_conversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var unavailableHandler = new TestHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        MainViewModel? viewModel = null;
        MainViewModel? restoredViewModel = null;
        MainWindow? window = null;
        try
        {
            viewModel = new MainViewModel(root, unavailableHandler);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            void PressModeShortcut() => window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.M,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift
            });

            PressModeShortcut();
            Assert.True(viewModel.IsPlanMode);
            Assert.False(viewModel.IsCodeTask);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Plan mode");

            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsGenerating))!
                .GetSetMethod(nonPublic: true)!.Invoke(viewModel, [true]);
            PressModeShortcut();
            Assert.True(viewModel.IsPlanMode);
            Assert.False(viewModel.IsCodeTask);
            Assert.Contains("Wait for the current response to finish", viewModel.ContextActionStatus, StringComparison.Ordinal);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsGenerating))!
                .GetSetMethod(nonPublic: true)!.Invoke(viewModel, [false]);

            PressModeShortcut();
            Assert.False(viewModel.IsPlanMode);
            Assert.False(viewModel.IsCodeTask);
            Assert.Contains("Code task is unavailable", viewModel.ContextActionStatus, StringComparison.Ordinal);

            PressModeShortcut();
            Assert.True(viewModel.IsPlanMode);
            var conversationId = Assert.IsType<Conversation>(viewModel.ActiveConversation).Id;
            window.Close();
            window = null;
            await StopAndFlushAsync(viewModel);
            viewModel = null;

            restoredViewModel = new MainViewModel(root,
                new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
            var restored = Assert.IsType<Conversation>(restoredViewModel.ActiveConversation);
            Assert.Equal(conversationId, restored.Id);
            Assert.True(restoredViewModel.IsPlanMode);
            Assert.False(restoredViewModel.IsCodeTask);
        }
        finally
        {
            window?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            if (restoredViewModel is not null) await StopAndFlushAsync(restoredViewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Icon_only_composer_action_exposes_a_descriptive_accessibility_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var viewModel = new MainViewModel(root);
        MainWindow? window = null;
        try
        {
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var sendButton = Assert.IsType<Button>(window.FindControl<Button>("ComposerSendButton"));
            Assert.Equal("Send or queue prompt; stop when the composer is empty", AutomationProperties.GetName(sendButton));
        }
        finally
        {
            window?.Close();
            await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Main_window_text_entries_and_selectors_expose_accessibility_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var viewModel = new MainViewModel(root);
        MainWindow? window = null;
        try
        {
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            Assert.Equal("Search conversations", AutomationProperties.GetName(Assert.IsType<TextBox>(window.FindControl<TextBox>("SearchTextBox"))));
            Assert.Equal("Message Codev", AutomationProperties.GetName(Assert.IsType<TextBox>(window.FindControl<TextBox>("ComposerTextBox"))));
            Assert.Equal("Model picker", AutomationProperties.GetName(Assert.IsType<ComboBox>(window.FindControl<ComboBox>("ModelPicker"))));
            Assert.Equal("Context size", AutomationProperties.GetName(Assert.IsType<ComboBox>(window.FindControl<ComboBox>("ContextSizePicker"))));
            Assert.Equal("Primary agent profile", AutomationProperties.GetName(Assert.IsType<ComboBox>(window.FindControl<ComboBox>("AgentProfileSelectionComboBox"))));
            var responseStyle = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), control => AutomationProperties.GetName(control) == "Response style");
            Assert.Equal("Response style", AutomationProperties.GetName(responseStyle));
        }
        finally
        {
            window?.Close();
            await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Context_size_picker_tracks_each_conversation_and_applies_model_cap()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Codev");
        Directory.CreateDirectory(appData);
        var largeContextConversation = new Conversation
        {
            Title = "Large context",
            Provider = "ollama",
            Model = "qwen3.6:35b-a3b",
            NumCtx = 32768,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var cappedConversation = new Conversation
        {
            Title = "Capped context",
            Provider = "ollama",
            Model = "qwen3-coder-next-q2-24k",
            NumCtx = 32768,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-conversations.json"),
            JsonSerializer.Serialize(new[] { largeContextConversation, cappedConversation }));
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-active-conversation.json"),
            JsonSerializer.Serialize(largeContextConversation.Id.ToString("D")));

        MainViewModel? viewModel = null;
        MainWindow? window = null;
        MainViewModel? restoredViewModel = null;
        MainWindow? restoredWindow = null;
        try
        {
            viewModel = new MainViewModel(root);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var picker = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("ContextSizePicker"));
            Assert.True(picker.IsEnabled);
            Assert.Equal(largeContextConversation.Id, viewModel.ActiveConversation!.Id);
            Assert.Equal(32768, picker.SelectedValue);

            picker.SelectedValue = 49152;
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal(49152, viewModel.ActiveConversation.NumCtx);

            var loadedCappedConversation = Assert.Single(viewModel.RecentConversations,
                conversation => conversation.Id == cappedConversation.Id);
            var loadedLargeContextConversation = Assert.Single(viewModel.RecentConversations,
                conversation => conversation.Id == largeContextConversation.Id);
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectConversationCommand.Execute(loadedCappedConversation));
            window.UpdateLayout();
            Assert.Equal(0, loadedCappedConversation.NumCtx);
            Assert.Equal(0, picker.SelectedValue);
            Assert.Equal(new[] { 0, 8192, 16384, 24576 }, viewModel.ContextSizes.Select(choice => choice.Value));

            picker.SelectedValue = 24576;
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal(24576, viewModel.ActiveConversation.NumCtx);

            await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectConversationCommand.Execute(loadedLargeContextConversation));
            window.UpdateLayout();
            Assert.Equal(49152, picker.SelectedValue);
            Assert.Equal(49152, viewModel.ActiveConversation.NumCtx);

            var persistence = typeof(MainViewModel).GetField("_persistenceTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel) as Task;
            Assert.NotNull(persistence);
            await persistence!.WaitAsync(TimeSpan.FromSeconds(5));

            window.Close();
            window = null;
            await StopAndFlushAsync(viewModel);
            viewModel = null;

            restoredViewModel = new MainViewModel(root);
            restoredWindow = new MainWindow { DataContext = restoredViewModel };
            restoredWindow.Show();
            restoredWindow.UpdateLayout();
            var restoredPicker = Assert.IsType<ComboBox>(restoredWindow.FindControl<ComboBox>("ContextSizePicker"));
            Assert.Equal(largeContextConversation.Id, restoredViewModel.ActiveConversation!.Id);
            Assert.Equal(49152, restoredPicker.SelectedValue);
            var restoredCappedConversation = Assert.Single(restoredViewModel.RecentConversations,
                conversation => conversation.Id == cappedConversation.Id);
            await Dispatcher.UIThread.InvokeAsync(() => restoredViewModel.SelectConversationCommand.Execute(restoredCappedConversation));
            restoredWindow.UpdateLayout();
            Assert.Equal(24576, restoredPicker.SelectedValue);
            Assert.Equal(new[] { 0, 8192, 16384, 24576 }, restoredViewModel.ContextSizes.Select(choice => choice.Value));
        }
        finally
        {
            window?.Close();
            restoredWindow?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            if (restoredViewModel is not null) await StopAndFlushAsync(restoredViewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Unknown_context_warning_is_visible_after_completed_ollama_turn_using_model_default()
    {
        var viewModel = new MainViewModel(Path.Combine(Path.GetTempPath(), "Codev-context-warning", Guid.NewGuid().ToString("N")));
        var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
        conversation.Provider = "ollama";
        conversation.Model = "qwen3.8:27b";
        conversation.NumCtx = 0;
        conversation.Messages.Add(new ChatMessage("user", "hi"));
        conversation.Messages.Add(new ChatMessage("assistant", "hello"));
        conversation.LastPromptProvider = "ollama";
        conversation.LastPromptModel = conversation.Model;
        conversation.LastPromptTokens = 12000;
        conversation.LastPromptContext = 0;
        var messageCounts = Assert.IsType<Dictionary<Guid, int>>(typeof(MainViewModel)
            .GetField("_lastPromptMessageCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel));
        messageCounts[conversation.Id] = conversation.Messages.Count;
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            var warning = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == viewModel.UnknownContextWarningLabel);
            Assert.True(warning.IsVisible);
            Assert.Contains("compact manually", warning.Text, StringComparison.OrdinalIgnoreCase);

            var setQueueProcessorRunning = typeof(MainViewModel).GetMethod("SetQueueProcessorRunning", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(setQueueProcessorRunning);
            setQueueProcessorRunning!.Invoke(viewModel, [true]);
            window.UpdateLayout();
            Assert.False(warning.IsVisible);
            setQueueProcessorRunning.Invoke(viewModel, [false]);
            window.UpdateLayout();
            Assert.True(warning.IsVisible);

            viewModel.Draft = "A new unsent question";
            window.UpdateLayout();
            Assert.False(warning.IsVisible);
            viewModel.Draft = "";
            window.UpdateLayout();
            Assert.True(warning.IsVisible);

            viewModel.ContextSize = 8192;
            window.UpdateLayout();
            Assert.False(warning.IsVisible);
        }
        finally
        {
            window.Close();
            await StopAndFlushAsync(viewModel);
        }
    }

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
            Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, viewModel.ProjectCommandPermissionMode);
            var settingsPersistenceTask = typeof(MainViewModel).GetField("_settingsPersistenceTask", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel) as Task
                ?? throw new InvalidOperationException("The footer mode selection did not start settings persistence.");
            await settingsPersistenceTask;
            var settingsPath = Path.Combine(appData, "avalonia-settings.json");
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
    public async Task New_private_code_task_workspace_runs_compound_command_without_approval_when_default_is_auto()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Codev");
        Directory.CreateDirectory(appData);
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-settings.json"),
            AvaloniaUiSettings.Serialize(AvaloniaUiSettings.Default with { DefaultProjectCommandPermissionMode = ProjectCommandPermissionMode.Auto }));
        MainViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            viewModel = new MainViewModel(root);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();
            Assert.Equal("Auto ▾", Assert.IsType<Button>(window.FindControl<Button>("ProjectCommandModeButton")).Content?.ToString());

            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            var enableCodeTask = typeof(MainViewModel).GetMethod("EnableCodeTaskWithWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Assert.IsAssignableFrom<Task>(enableCodeTask.Invoke(viewModel, [conversation]));
            Assert.True(conversation.IsCodeTask);
            Assert.NotNull(conversation.ProjectPath);
            Assert.Equal(ProjectCommandPermissionMode.Auto, viewModel.ProjectCommandPermissionMode);

            var approvalRequests = 0;
            viewModel.ApproveProjectCommandAsync = _ =>
            {
                approvalRequests++;
                return Task.FromResult(ProjectCommandApprovalChoice.Cancel);
            };
            var approvePolicy = typeof(MainViewModel).GetMethod("ApproveCommandWithProjectPolicyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var proposal = new CodeTaskCommandProposal(
                "Remove-Item -Recurse -Force space-invaders-game/signaling; git -C space-invaders-game status --short",
                conversation.ProjectPath!, "PowerShell");
            var pendingApproval = Assert.IsAssignableFrom<Task<CommandApprovalOutcome>>(
                approvePolicy.Invoke(viewModel, [proposal, Array.Empty<string>()]));

            Assert.Equal(CommandApprovalOutcome.Approved, await pendingApproval);
            Assert.Equal(0, approvalRequests);
            Assert.False(Assert.IsType<Border>(window.FindControl<Border>("InlineApprovalPanel")).IsVisible);
        }
        finally
        {
            window?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Best_of_n_is_a_one_shot_local_code_task_choice_captured_by_the_queue()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-best-of-n-ui", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "app-data");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var viewModel = new MainViewModel(appData);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            viewModel.SetProjectFolder(project);
            await viewModel.TrustProjectFolderAsync(project);
            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            conversation.Provider = "ollama";
            conversation.Model = "qwen-test";
            typeof(MainViewModel).GetMethod("SetConversationMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [ConversationMode.CodeTask]);
            Assert.True(viewModel.CanSelectBestOfNAttempts);

            window.Show();
            window.UpdateLayout();
            var contextButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.Content?.ToString() == "＋ Context");
            var flyout = Assert.IsType<MenuFlyout>(contextButton.Flyout);
            flyout.ShowAt(contextButton);
            window.UpdateLayout();
            var bestOfMenu = Assert.Single(flyout.Items.OfType<MenuItem>(),
                item => item.Header?.ToString()?.StartsWith("Best-of-N", StringComparison.Ordinal) == true);
            Assert.True(bestOfMenu.IsEnabled);
            Assert.Contains(bestOfMenu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "3 independent attempts");

            viewModel.SetBestOfNAttemptsForNextTurn(3);
            Assert.Equal(3, conversation.BestOfNAttempts);
            typeof(MainViewModel).GetField("_queuePaused", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, true);
            typeof(MainViewModel).GetField("_queueProcessorRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, true);
            viewModel.Draft = "inspect the project";
            Assert.True(conversation.IsCodeTask);
            Assert.Equal(project, conversation.ProjectPath);
            Assert.Equal("ollama", conversation.Provider);
            Assert.Equal("inspect the project", viewModel.Draft);
            await Dispatcher.UIThread.InvokeAsync(async () =>
                await (Task)typeof(MainViewModel).GetMethod("SendDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(viewModel, null)!);

            Assert.True(conversation.PendingTurns?.Count > 0,
                $"{viewModel.ContextActionStatus}; draft={viewModel.Draft}; requests={conversation.PendingRequestCount}; active={ReferenceEquals(viewModel.ActiveConversation, conversation)}; busy={viewModel.IsGenerating}; paused={viewModel.IsQueuePaused}");
            Assert.Equal(3, Assert.Single(conversation.PendingTurns!).BestOfNAttempts);
            Assert.Equal(1, conversation.BestOfNAttempts);
        }
        finally
        {
            typeof(MainViewModel).GetField("_queueProcessorRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, false);
            window.Close();
            await StopAndFlushAsync(viewModel);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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

    [AvaloniaFact]
    public async Task Loads_previous_public_release_conversation_store_and_active_selection()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Codev");
        Directory.CreateDirectory(appData);
        var conversationId = Guid.Parse("dbb54864-8ab8-42c5-8b55-3a1e55c7c04b");
        // These persisted fields match the v2026.10.01 conversation/settings schema. Newer
        // delegation, agent-profile, and retrieval fields are intentionally absent.
        var previousReleaseStore = $$"""
            [{
              "Id": "{{conversationId:D}}",
              "Title": "Previous release conversation",
              "Draft": "unsent legacy draft",
              "Model": "qwen3.6:35b-a3b",
              "Provider": "ollama",
              "IsPlanMode": false,
              "IsCodeTask": true,
              "ThinkEnabled": false,
              "OutputStyle": "Balanced",
              "NumCtx": 8192,
              "Temperature": 0.2,
              "LastPromptTokens": 42,
              "LastPromptContext": 8192,
              "LastPromptModel": "qwen3.6:35b-a3b",
              "LastPromptProvider": "ollama",
              "IsPinned": true,
              "IsArchived": false,
              "UpdatedAt": "2026-10-01T14:54:37+00:00",
              "Messages": [
                { "Role": "user", "Content": "old request", "Thinking": "" },
                { "Role": "assistant", "Content": "old reply", "Thinking": "" }
              ],
              "FileChanges": [
                { "RelativePath": "game.js", "CheckpointPath": null, "ChangedAt": "2026-10-01T14:55:00+00:00", "Kind": "Edit", "PreviousFileExisted": true }
              ],
              "ContextFiles": ["README.md"],
              "TaskChecklist": [],
              "PendingTurns": []
            }]
            """;
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-conversations.json"), previousReleaseStore);
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-active-conversation.json"),
            JsonSerializer.Serialize(conversationId.ToString("D")));
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-settings.json"),
            """{"Theme":"light","OllamaEndpoint":"http://localhost:11434/","PromptTemplates":null,"SamplingPresets":null,"ReadingWidth":960}""");

        MainViewModel? viewModel = null;
        MainWindow? window = null;
        try
        {
            viewModel = new MainViewModel(root);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var restored = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            Assert.Equal(conversationId, restored.Id);
            Assert.Equal("Previous release conversation", restored.Title);
            Assert.Equal("unsent legacy draft", restored.Draft);
            Assert.Equal("qwen3.6:35b-a3b", restored.Model);
            Assert.Equal(8192, restored.NumCtx);
            Assert.Equal(42, restored.LastPromptTokens);
            Assert.Equal(["old request", "old reply"], viewModel.Messages.Select(message => message.Content));
            Assert.Equal("game.js", Assert.Single(restored.FileChanges).RelativePath);
            Assert.Null(Assert.Single(restored.FileChanges).TurnUserMessageIndex);
            Assert.Null(restored.ParentConversationId);
            Assert.Null(restored.AgentProfileName);
            Assert.Empty(restored.TaskChecklist);
            Assert.False(restored.EnableSemanticSearch);
            Assert.Equal(ProjectCommandPermissionMode.Auto, viewModel.ProjectCommandPermissionMode);
            Assert.False(viewModel.IsDarkTheme);
            Assert.True(viewModel.IsWideReadingWidth);
        }
        finally
        {
            window?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Debug_profile_ask_rule_does_not_prompt_in_auto_mode()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var viewModel = new MainViewModel(root);
        MainWindow? window = null;
        try
        {
            viewModel.SetProjectFolder(project);
            await viewModel.TrustProjectFolderAsync(project);
            await viewModel.SetProjectCommandPermissionModeAsync(ProjectCommandPermissionMode.Auto);
            typeof(MainViewModel).GetMethod("SetConversationMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [ConversationMode.CodeTask]);
            Assert.Contains("configured MCP tools", viewModel.CodeTaskTooltip, StringComparison.OrdinalIgnoreCase);
            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            var debugProfile = AgentProfileCatalog.BuiltInProfiles.Single(profile => profile.Name == "Debug");
            var profilePrompts = 0;
            var commandPrompts = 0;
            viewModel.ConfirmAgentProfileToolAsync = (_, _, _) =>
            {
                profilePrompts++;
                return Task.FromResult(false);
            };
            viewModel.ApproveProjectCommandAsync = _ =>
            {
                commandPrompts++;
                return Task.FromResult(ProjectCommandApprovalChoice.Cancel);
            };
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var profilePermission = typeof(MainViewModel).GetMethod("CheckAgentProfileToolPermissionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var projectApproval = typeof(MainViewModel).GetMethod("ApproveCommandWithProjectPolicyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(project), conversation,
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                permissionApproval: proposal => Dispatcher.UIThread.InvokeAsync(async () =>
                    await (Task<CommandApprovalOutcome>)projectApproval.Invoke(viewModel, [proposal, Array.Empty<string>()])!),
                agentProfilePermission: (name, args) =>
                    (Task<AgentToolProfileDecision>)profilePermission.Invoke(viewModel, [conversation, debugProfile, name, args])!,
                agentProfile: debugProfile,
                permissionProjectPath: project);
            using var arguments = JsonDocument.Parse("""{"command":"dotnet --version"}""");

            var result = await executor.ExecuteAsync("verify_command", arguments.RootElement.Clone());

            Assert.Contains("Verification PASSED (exit code 0)", result, StringComparison.Ordinal);
            Assert.Equal(0, profilePrompts);
            Assert.Equal(0, commandPrompts);
            Assert.False(Assert.IsType<Border>(window.FindControl<Border>("InlineApprovalPanel")).IsVisible);

        }
        finally
        {
            window?.Close();
            await StopAndFlushAsync(viewModel);
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
    public async Task Main_window_waits_for_background_command_shutdown_before_closing()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var viewModel = new MainViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        var backgroundCommands = (BackgroundCommandManager)typeof(MainViewModel)
            .GetField("_backgroundCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        var persistGate = (SemaphoreSlim)typeof(MainViewModel)
            .GetField("_persistGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        var owner = viewModel.ActiveConversation!.Id;
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var started = await backgroundCommands.StartAsync(owner, command, root, ShellCommandResolver.ResolveCurrent());
        var closedStatus = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closedStatus.TrySetResult(backgroundCommands.Read(owner, started.Id)?.Status);
        var persistGateHeld = false;

        try
        {
            window.Show();
            persistGate.Wait();
            persistGateHeld = true;
            window.Close();

            Assert.True(window.IsVisible, "The window should remain open while its asynchronous shutdown work is pending.");

            persistGate.Release();
            persistGateHeld = false;
            Assert.Equal("Exited", await closedStatus.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            if (persistGateHeld) persistGate.Release();
            window.Close();
            await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
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
    public async Task Mcp_executor_uses_inline_approval_and_run_once_does_not_save_a_rule()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var viewModel = new MainViewModel(root);
        MainWindow? window = null;
        try
        {
            viewModel.SetProjectFolder(project);
            await viewModel.SetProjectCommandPermissionModeAsync(ProjectCommandPermissionMode.AskEveryTime);
            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""");
            using var arguments = JsonDocument.Parse("""{"query":"codev"}""");
            var tool = new McpCodeTaskTool("mcp_github_search_0123456789abcdef", "github", "GitHub", "search",
                "Search repositories.", schema.RootElement.Clone(), null!);
            var approve = typeof(MainViewModel).GetMethod("ApproveMcpToolWithProjectPolicyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var calls = 0;
            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(project), conversation,
                _ => Task.FromResult(false), _ => Task.FromResult(false),
                mcpTools: new Dictionary<string, McpCodeTaskTool> { [tool.FunctionName] = tool },
                mcpPermissionApproval: (candidate, candidateArguments, profileApproved) =>
                    (Task<CommandApprovalOutcome>)approve.Invoke(viewModel,
                        [conversation, candidate, candidateArguments, profileApproved])!,
                mcpCall: (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult("safe MCP result");
                });

            var canceledRequest = executor.ExecuteAsync(tool.FunctionName, arguments.RootElement.Clone());
            await Dispatcher.UIThread.InvokeAsync(() => { });
            var content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            var cancel = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Cancel");
            await Dispatcher.UIThread.InvokeAsync(() => cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            var canceledResult = await canceledRequest.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("MCP tool call rejected", canceledResult, StringComparison.Ordinal);
            Assert.Equal(0, calls);

            var allowedRequest = executor.ExecuteAsync(tool.FunctionName, arguments.RootElement.Clone());
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(window.FindControl<Border>("InlineApprovalPanel")!.IsVisible);
            content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            var runOnce = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Run once");
            await Dispatcher.UIThread.InvokeAsync(() => runOnce.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            var allowedResult = await allowedRequest.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("safe MCP result", allowedResult, StringComparison.Ordinal);
            Assert.Equal(1, calls);
            Assert.False(window.FindControl<Border>("InlineApprovalPanel")!.IsVisible);
            var permissionRegistry = ProjectMcpToolPermissionRegistry.Load(Path.Combine(root, "Codev", "avalonia-mcp-permissions.json"));
            Assert.Equal(ProjectCommandPermissionDecision.Ask,
                permissionRegistry.Evaluate(project, ProjectCommandPermissionMode.AskEveryTime, tool.ServerId, tool.ToolName));
        }
        finally
        {
            window?.Close();
            await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
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
            picker.SelectedItem = viewModel.AgentProfiles.First(profile => profile.Name == "Reviewer");
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.Equal("Reviewer", viewModel.SelectedAgentProfileName);
            Assert.Equal("Reviewer", conversation.AgentProfileName);
            Assert.Equal("Reviewer agent", viewModel.PrimaryAgentLabel);

            picker.SelectedItem = viewModel.AgentProfiles.First(profile => profile.Name == "Plan");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("Plan", conversation.AgentProfileName);
            Assert.Equal("Plan agent", viewModel.PrimaryAgentLabel);

            picker.SelectedItem = viewModel.AgentProfiles.First(profile => profile.Name.Length == 0);
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
    public async Task Code_task_profile_selection_is_restored_after_restarting_the_window()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var store = new UserAgentProfileStore(Path.Combine(root, "Codev", "agents"));
        await store.SaveAsync("smoke-qa.md", "---\nname: Smoke QA\ndescription: Review source changes.\n---\nInspect first, then report clearly.\n");

        MainViewModel? viewModel = null;
        MainWindow? window = null;
        MainViewModel? restoredViewModel = null;
        MainWindow? restoredWindow = null;
        try
        {
            viewModel = new MainViewModel(root);
            typeof(MainViewModel).GetMethod("SetConversationMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [ConversationMode.CodeTask]);
            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();
            await viewModel.RefreshAgentProfilesAsync();

            var picker = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("AgentProfileSelectionComboBox"));
            Assert.Equal("", viewModel.SelectedAgentProfileName);
            var reviewerIndex = viewModel.AgentProfiles.ToList().FindIndex(profile => profile.Name == "Smoke QA");
            Assert.True(reviewerIndex > 0);
            var pickerCenter = global::Avalonia.VisualExtensions.TranslatePoint(picker,
                new global::Avalonia.Point(picker.Bounds.Width / 2, picker.Bounds.Height / 2), window);
            Assert.NotNull(pickerCenter);
            window.MouseDown(pickerCenter.Value, MouseButton.Left);
            window.MouseUp(pickerCenter.Value, MouseButton.Left);
            window.UpdateLayout();
            Assert.True(picker.IsDropDownOpen);
            picker.IsDropDownOpen = false;
            picker.SelectedItem = viewModel.AgentProfiles.First(profile => profile.Name == "Smoke QA");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("Smoke QA", viewModel.ActiveConversation!.AgentProfileName);

            var persistence = typeof(MainViewModel).GetField("_persistenceTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel) as Task;
            Assert.NotNull(persistence);
            await persistence!.WaitAsync(TimeSpan.FromSeconds(5));

            window.Close();
            window = null;
            await StopAndFlushAsync(viewModel);
            viewModel = null;

            restoredViewModel = new MainViewModel(root);
            restoredWindow = new MainWindow { DataContext = restoredViewModel };
            restoredWindow.Show();
            restoredWindow.UpdateLayout();
            await restoredViewModel.RefreshAgentProfilesAsync();

            var restoredPicker = Assert.IsType<ComboBox>(restoredWindow.FindControl<ComboBox>("AgentProfileSelectionComboBox"));
            Assert.Equal("Smoke QA", restoredViewModel.SelectedAgentProfileName);
            Assert.Equal("Smoke QA", Assert.IsType<AgentProfileChoice>(restoredPicker.SelectedItem).Name);
            Assert.Equal("Smoke QA agent", restoredViewModel.PrimaryAgentLabel);
        }
        finally
        {
            window?.Close();
            restoredWindow?.Close();
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            if (restoredViewModel is not null) await StopAndFlushAsync(restoredViewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Agent_profile_picker_reselects_the_saved_choice_after_catalog_items_are_replaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", "agent-profile-rebind", Guid.NewGuid().ToString("N"));
        var viewModel = new MainViewModel(root);
        MainWindow? window = null;
        try
        {
            await viewModel.RefreshAgentProfilesAsync();
            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            conversation.IsCodeTask = true;
            conversation.AgentProfileName = "Smoke QA";
            var loadingChoice = new AgentProfileChoice("Smoke QA", "Smoke QA · loading", "Restoring saved profile.");
            viewModel.AgentProfiles.Add(loadingChoice);
            var notify = typeof(MainViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: [typeof(string)], modifiers: null)!;
            notify.Invoke(viewModel, [nameof(MainViewModel.SelectedAgentProfileChoice)]);

            window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();
            var picker = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("AgentProfileSelectionComboBox"));
            Assert.Same(loadingChoice, picker.SelectedItem);

            var loadedChoice = new AgentProfileChoice("Smoke QA", "Smoke QA", "Review source changes.");
            typeof(MainViewModel).GetField("_agentProfilesLoadedForConversationId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, null);
            viewModel.AgentProfiles.Clear();
            viewModel.AgentProfiles.Add(new AgentProfileChoice("", "Build", "Default profile."));
            viewModel.AgentProfiles.Add(loadedChoice);
            typeof(MainViewModel).GetField("_agentProfilesLoadedForConversationId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, conversation.Id);
            notify.Invoke(viewModel, [nameof(MainViewModel.SelectedAgentProfileChoice)]);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.Same(loadedChoice, picker.SelectedItem);
            Assert.Equal("Smoke QA", viewModel.SelectedAgentProfileName);
            Assert.Equal("Smoke QA", conversation.AgentProfileName);
        }
        finally
        {
            window?.Close();
            await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
        }
    }

    [AvaloniaFact]
    public async Task Older_agent_profile_refresh_cannot_replace_profiles_after_project_switch()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-profile-refresh-race", Guid.NewGuid().ToString("N"));
        var projectA = Path.Combine(root, "project-a");
        var projectB = Path.Combine(root, "project-b");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        var oldLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MainViewModel? viewModel = null;
        Task? oldRefresh = null;
        try
        {
            viewModel = new MainViewModel(root);
            var oldProfile = CreateProfile("Project A Reviewer", projectA);
            var newProfile = CreateProfile("Project B Reviewer", projectB);
            async Task<AgentProfileLoadResult> LoadProfilesAsync(string profilesDirectory, string? projectPath, bool includeProjectProfiles,
                CancellationToken cancellationToken, IReadOnlyList<string>? additionalDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(projectPath, projectA, StringComparison.Ordinal))
                {
                    oldLoadStarted.TrySetResult();
                    await releaseOldLoad.Task;
                    return new AgentProfileLoadResult([oldProfile], []);
                }
                return new AgentProfileLoadResult([newProfile], []);
            }

            typeof(MainViewModel).GetField("_loadAgentProfilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, (Func<string, string?, bool, CancellationToken, IReadOnlyList<string>?, Task<AgentProfileLoadResult>>)LoadProfilesAsync);

            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            conversation.ProjectPath = projectA;
            oldRefresh = viewModel.RefreshAgentProfilesAsync();
            await oldLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            conversation.ProjectPath = projectB;
            await viewModel.RefreshAgentProfilesAsync();
            Assert.Contains(viewModel.AgentProfiles, profile => profile.Name == newProfile.Name);

            releaseOldLoad.TrySetResult();
            await oldRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.Contains(viewModel.AgentProfiles, profile => profile.Name == newProfile.Name);
            Assert.DoesNotContain(viewModel.AgentProfiles, profile => profile.Name == oldProfile.Name);
        }
        finally
        {
            releaseOldLoad.TrySetResult();
            if (oldRefresh is not null) await oldRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            if (viewModel is not null) await StopAndFlushAsync(viewModel);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static AgentProfile CreateProfile(string name, string projectPath) => new(name, "Test profile", null, null, null,
            AgentToolPermission.Allow, new Dictionary<string, AgentToolPermission>(), "Test instructions", "project",
            Path.Combine(projectPath, ".codev", "agents", "reviewer.md"), Mode: "primary");
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
                ["index.html", "", "<h1>Updated</h1>", true, null, null, "No attempt passed verification, so Auto will not apply this candidate automatically. Review each file before applying it."]));
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
            Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.Contains("No attempt passed verification, so Auto will not apply this candidate automatically", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(review.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.Contains("Auto paused", StringComparison.Ordinal) == true);
            Assert.Contains(review.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Keep unchanged");
            Assert.Contains(review.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Approve & create");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task File_review_reports_the_actual_reason_and_auto_applies_ordinary_proposals()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-mode-ui", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var appData = Path.Combine(root, "app-data");
        Directory.CreateDirectory(project);
        var viewModel = new MainViewModel(appData);
        try
        {
            viewModel.SetProjectFolder(project);
            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            var reviewCalls = 0;
            string? approvalReason = null;
            viewModel.ReviewFileChangeAsync = (_, _, _, _, _, _, reason) =>
            {
                reviewCalls++;
                approvalReason = reason;
                return Task.FromResult(false);
            };
            var reviewChange = typeof(MainViewModel).GetMethod("ReviewOrAutoApplyFileChangeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var proposal = new CodeTaskFileProposal("example.txt", "old", "new", false);

            await viewModel.SetProjectCommandPermissionModeAsync(ProjectCommandPermissionMode.AskEveryTime);
            Assert.False(await (Task<bool>)reviewChange.Invoke(viewModel, [conversation, proposal])!);
            Assert.Equal(1, reviewCalls);
            Assert.Contains("Ask every time", approvalReason, StringComparison.Ordinal);

            await viewModel.SetProjectCommandPermissionModeAsync(ProjectCommandPermissionMode.Auto);
            Assert.True(await (Task<bool>)reviewChange.Invoke(viewModel, [conversation, proposal])!);
            Assert.Equal(1, reviewCalls);
        }
        finally
        {
            await StopAndFlushAsync(viewModel);
            await DeleteAutoModeTestDirectoryAsync(root);
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

    private sealed class TestHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
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

    private static async Task WaitForContextActionStatusAsync(MainViewModel viewModel, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            if (viewModel.ContextActionStatus.Contains(expected, StringComparison.Ordinal)) return;
            await Task.Delay(10);
        }
        Assert.Contains(expected, viewModel.ContextActionStatus, StringComparison.Ordinal);
    }

    private sealed class FakeConversationBackupPicker : IConversationBackupPicker
    {
        public bool CanOpen => true;
        public bool CanSave => true;
        public MemoryStorageFile File { get; } = new("backup.codev.json");
        public FilePickerSaveOptions? LastSaveOptions { get; private set; }
        public FilePickerOpenOptions? LastOpenOptions { get; private set; }

        public Task<IConversationBackupFile?> SaveFilePickerAsync(FilePickerSaveOptions options)
        {
            LastSaveOptions = options;
            return Task.FromResult<IConversationBackupFile?>(File);
        }

        public Task<IReadOnlyList<IConversationBackupFile>> OpenFilePickerAsync(FilePickerOpenOptions options)
        {
            LastOpenOptions = options;
            return Task.FromResult<IReadOnlyList<IConversationBackupFile>>([File]);
        }
    }

    private sealed class MemoryStorageFile(string name) : IConversationBackupFile
    {
        private byte[] _contents = [];
        public string Name => name;

        public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(new MemoryStream(_contents, writable: false));

        public Task<Stream> OpenWriteAsync()
        {
            var stream = new MemoryStream();
            return Task.FromResult<Stream>(new CommitOnDisposeStream(stream, contents => _contents = contents));
        }

        public string ReadAllText() => Encoding.UTF8.GetString(_contents);
    }

    private sealed class CommitOnDisposeStream(MemoryStream inner, Action<byte[]> commit) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commit(inner.ToArray());
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            commit(inner.ToArray());
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
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

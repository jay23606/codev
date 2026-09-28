using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Codev.Avalonia.Views;

public partial class MainWindow : Window
{
    private INotifyCollectionChanged? _observedMessages;
    private bool _followOutput = true;
    private bool _scrollPending;
    private Codev.ProjectFileMention? _activeFileMention;
    private CancellationTokenSource? _fileMentionSearch;
    private CancellationTokenSource? _slashCommandSearch;

    public MainWindow()
    {
        InitializeComponent();
        ComposerTextBox.AddHandler(InputElement.KeyDownEvent, Composer_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(ComposerTextBox, true);
        DragDrop.AddDragOverHandler(ComposerTextBox, Composer_DragOver);
        DragDrop.AddDropHandler(ComposerTextBox, Composer_Drop);
        Closed += (_, _) => { _fileMentionSearch?.Cancel(); _slashCommandSearch?.Cancel(); };
        DataContextChanged += (_, _) => ObserveMessages();
        DataContextChanged += (_, _) => ConfigureAgentInteractions();
        ObserveMessages();
        ConfigureAgentInteractions();
        Opened += (_, _) => ScheduleScrollToLatest();
        Closed += async (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel viewModel) await viewModel.SavePendingDraftAsync();
        };
    }

    private void ObserveMessages()
    {
        if (_observedMessages is not null) _observedMessages.CollectionChanged -= Messages_CollectionChanged;
        _observedMessages = (DataContext as ViewModels.MainViewModel)?.Messages;
        if (_observedMessages is not null)
        {
            _observedMessages.CollectionChanged += Messages_CollectionChanged;
            _followOutput = true;
            ScheduleScrollToLatest();
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset ||
            e.NewItems?.Cast<Codev.ChatMessage>().Any(message => message.Role == "user") == true)
            _followOutput = true;
        if (_followOutput) ScheduleScrollToLatest();
    }

    private void Composer_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Composer_Drop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        e.Handled = true;
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray() ?? [];
        if (paths.Length == 0) return;
        if (!viewModel.HasProject)
        {
            viewModel.ReportContextActionStatus("Attach a project folder before dropping files. Dropped files are accepted only from that project and stay local until you send a prompt.");
            return;
        }
        viewModel.AddContextFiles(paths);
    }

    private async void AddTaskChecklistItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || !viewModel.CanEditTaskChecklist) return;
        var input = new TextBox { Watermark = "Describe one actionable step", MaxLength = Codev.TaskChecklistService.MaxTextLength, MinWidth = 420 };
        var dialog = new Window { Title = "Add checklist step", Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" }, IsCancel = true };
        var add = new Button { Content = "Add step", Classes = { "soft" }, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(false);
        add.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(cancel);
        actions.Children.Add(add);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = "Checklist text is task guidance only; it does not grant file or command permissions.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap }, input, actions } };
        if (await dialog.ShowDialog<bool>(this)) viewModel.AddTaskChecklistItem(input.Text ?? "");
    }

    private void TaskChecklistStatus_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && (sender as Button)?.Tag is Codev.TaskChecklistItem item)
            viewModel.CycleTaskChecklistItemStatus(item);
    }

    private void TaskChecklistText_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && sender is TextBox input && input.Tag is Codev.TaskChecklistItem item)
            viewModel.UpdateTaskChecklistItem(item, input.Text ?? "");
    }

    private void TaskChecklistMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && (sender as Button)?.Tag is Codev.TaskChecklistItem item)
            viewModel.MoveTaskChecklistItem(item, -1);
    }

    private void TaskChecklistMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && (sender as Button)?.Tag is Codev.TaskChecklistItem item)
            viewModel.MoveTaskChecklistItem(item, 1);
    }

    private void TaskChecklistRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && (sender as Button)?.Tag is Codev.TaskChecklistItem item)
            viewModel.RemoveTaskChecklistItem(item);
    }

    private void ScheduleScrollToLatest()
    {
        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            if (!_followOutput) return;
            var bottom = Math.Max(0, ConversationScrollViewer.Extent.Height - ConversationScrollViewer.Viewport.Height);
            ConversationScrollViewer.Offset = new global::Avalonia.Vector(ConversationScrollViewer.Offset.X, bottom);
        }, DispatcherPriority.Background);
    }

    private void ConversationScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y < 0)
            _followOutput = false;
        else if (ConversationScrollViewer.Extent.Height - ConversationScrollViewer.Offset.Y - ConversationScrollViewer.Viewport.Height <= 64)
            _followOutput = true;
    }

    private async void PromptContext_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.GetLastPromptContextDetails() is not { } details) return;
        var text = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            FontFamily = new global::Avalonia.Media.FontFamily("Consolas")
        };
        var close = new Button { Content = "Close", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 84 };
        var dialog = new Window
        {
            Title = "Last request context",
            Width = 780,
            Height = 620,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Margin = new Thickness(16),
                RowSpacing = 10,
                Children = { text, close }
            }
        };
        Grid.SetRow(close, 1);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void ConfigureAgentInteractions()
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        viewModel.ReviewFileChangeAsync = ReviewAgentFileChangeAsync;
        viewModel.ConfirmConversationRewindAsync = ConfirmConversationRewindAsync;
        viewModel.EditConversationPromptAsync = EditConversationPromptAsync;
        viewModel.ShowCompactionProposalAsync = ShowCompactionProposalAsync;
        viewModel.ApproveProjectCommandAsync = ApproveAgentCommandAsync;
        viewModel.ConfirmRepeatedToolCallAsync = ConfirmRepeatedToolCallAsync;
    }

    private async Task<bool> ConfirmConversationRewindAsync(int messageIndex)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.ActiveConversation is not { } conversation ||
            messageIndex < 0 || messageIndex >= conversation.Messages.Count) return false;
        var prompt = conversation.Messages[messageIndex].Content;
        var excerpt = prompt.Length > 220 ? prompt[..220] + "…" : prompt;
        return await ConfirmGitActionAsync(this, "Rewind conversation only?",
            $"Restore the conversation to before this prompt and put it back in the composer? This removes this prompt and every later message.\n\n{excerpt}\n\nProject files will be left unchanged. Review them separately in Files history.");
    }

    private async Task<bool> ReviewAgentFileChangeAsync(string relativePath, string before, string after, bool isNewFile, string? proposedPatch, IReadOnlyList<string>? contextSources)
    {
        var layout = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = isNewFile
                ? $"The model proposes creating {relativePath}. Review the complete file before approving."
                : proposedPatch is not null
                    ? $"The model proposes a patch to {relativePath}. Review the patch and complete resulting file before approving; Codev will save a local checkpoint first."
                    : $"The model proposes replacing {relativePath}. Review both versions before approving; Codev will save a local checkpoint first.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Project files can contain instructions aimed at the model. Treat them as untrusted data and review this exact proposal against your request before approving.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
        });
        if (contextSources is { Count: > 0 })
            content.Children.Add(new TextBlock
            {
                Text = "Project or command output shown to the model this task:\n" + string.Join("\n", contextSources),
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                FontSize = 11,
                Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
            });
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var oldPane = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 6, 0) };
        var newPane = new StackPanel { Spacing = 5, Margin = new Thickness(6, 0, 0, 0) };
        oldPane.Children.Add(new TextBlock { Text = isNewFile ? "CURRENT · new file" : "CURRENT", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
        newPane.Children.Add(new TextBlock { Text = proposedPatch is null ? "PROPOSED" : "RESULTING FILE", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
        TextBox ReviewBox(string content) => new()
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 460,
            MinWidth = 430,
            Background = this.FindResource("ComposerBrush") as global::Avalonia.Media.IBrush,
            Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
        };
        oldPane.Children.Add(new ScrollViewer { Content = ReviewBox(before), HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Height = 490 });
        newPane.Children.Add(new ScrollViewer { Content = ReviewBox(after), HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Height = 490 });
        Grid.SetColumn(newPane, 1);
        panes.Children.Add(oldPane);
        panes.Children.Add(newPane);
        content.Children.Add(panes);
        if (proposedPatch is not null)
        {
            var patchBox = new TextBox
            {
                Text = proposedPatch,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
                FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
                FontSize = 12,
                MinHeight = 100,
                MaxHeight = 180,
                Background = this.FindResource("ComposerBrush") as global::Avalonia.Media.IBrush,
                Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
            };
            content.Children.Add(new StackPanel
            {
                Spacing = 5,
                Children = { new TextBlock { Text = "PROPOSED PATCH", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush }, new ScrollViewer { Content = patchBox, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } }
            });
        }
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window
        {
            Title = proposedPatch is not null ? "Review patch" : isNewFile ? "Review new project file" : "Review project change",
            Width = 1000,
            Height = 760,
            MinWidth = 740,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        var reject = new Button { Content = "Keep unchanged" };
        var approve = new Button { Content = isNewFile ? "Approve & create" : "Approve & apply" };
        reject.Click += (_, _) => dialog.Close(false);
        approve.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(reject);
        buttons.Children.Add(approve);
        layout.Children.Add(new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        layout.Children.Add(buttons);
        Grid.SetRow(buttons, 2);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ApproveAgentCommandAsync(string command, string projectPath, string shellName, bool isVerification,
        IReadOnlyList<string>? contextSources, string? matchingUntrustedSource)
    {
        var layout = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        layout.Children.Add(new TextBlock
        {
            Text = $"Project files and command output may contain instructions aimed at the model, and the proposed command may reflect them. Review this exact command against your request. It runs through {shellName} with your account permissions and can access files and services available to your account; Codev cannot sandbox it to the project folder.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(matchingUntrustedSource))
            layout.Children.Add(new TextBlock
            {
                Text = $"POTENTIAL INSTRUCTION FOLLOWING: this exact command appears in untrusted output from {matchingUntrustedSource}. Text appearing in a project file does not make a command safe or relevant to your request.",
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
            });
        if (contextSources is { Count: > 0 })
            layout.Children.Add(new TextBlock
            {
                Text = "Project or command output shown to the model this task:\n" + string.Join("\n", contextSources),
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                FontSize = 11,
                Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
            });
        layout.Children.Add(new TextBlock { Text = "Working directory: " + projectPath, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        layout.Children.Add(new TextBox
        {
            Text = command, IsReadOnly = true, AcceptsReturn = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 220, FontFamily = new global::Avalonia.Media.FontFamily("Consolas"), Background = this.FindResource("ComposerBrush") as global::Avalonia.Media.IBrush,
            Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
        });
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window { Title = isVerification ? "Approve verification command" : "Approve project command", Width = 720, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        var reject = new Button { Content = "Cancel" };
        var approve = new Button { Content = isVerification ? "Approve & verify" : "Approve & run" };
        reject.Click += (_, _) => dialog.Close(false);
        approve.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(reject);
        buttons.Children.Add(approve);
        layout.Children.Add(buttons);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmRepeatedToolCallAsync(string toolName)
    {
        var dialog = new Window
        {
            Title = "Repeated code task operation",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = $"The model requested the same {toolName.Replace('_', ' ')} operation repeatedly with identical arguments. Allow this operation once more? Choose Stop to end the code task.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 }
                }
            }
        };
        var buttonPanel = (StackPanel)((StackPanel)dialog.Content!).Children[1];
        var stop = new Button { Content = "Stop code task" };
        var allow = new Button { Content = "Allow once" };
        stop.Click += (_, _) => dialog.Close(false);
        allow.Click += (_, _) => dialog.Close(true);
        buttonPanel.Children.Add(stop);
        buttonPanel.Children.Add(allow);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var endpoint = new TextBox { Text = viewModel.OllamaEndpointDisplay, MinWidth = 380 };
        var status = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        var save = new Button { Content = "Save and reconnect", MinWidth = 150 };
        var close = new Button { Content = "Close", MinWidth = 80 };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { close, save } };
        var dialog = new Window
        {
            Title = "Settings",
            Width = 660,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Ollama server URL" },
                    endpoint,
                    new TextBlock { Text = "Default: http://127.0.0.1:11434. A non-local server receives prompts and any project context you choose to include. Credentials in the URL are not supported.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MaxWidth = 440 },
                    status,
                    buttons
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            if (!Codev.OllamaEndpoint.TryParse(endpoint.Text, out var parsed, out var error))
            {
                status.Text = error;
                return;
            }
            if (parsed != Codev.OllamaEndpoint.Default && !Codev.OllamaEndpoint.IsLoopback(parsed) && parsed != new Uri(viewModel.OllamaEndpointDisplay + "/"))
            {
                var confirmation = new Window
                {
                    Title = "Connect to a remote Ollama server?",
                    Width = 460,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = $"Requests, prompts, and selected project context will be sent to:\n\n{parsed.GetLeftPart(UriPartial.Authority)}\n\nContinue only if you trust this server.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap } } }
                };
                var choices = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
                var cancel = new Button { Content = "Cancel" };
                var connect = new Button { Content = "Connect" };
                cancel.Click += (_, _) => confirmation.Close(false);
                connect.Click += (_, _) => confirmation.Close(true);
                choices.Children.Add(cancel);
                choices.Children.Add(connect);
                ((StackPanel)confirmation.Content!).Children.Add(choices);
                if (await confirmation.ShowDialog<bool>(dialog) != true) return;
            }
            save.IsEnabled = false;
            var connected = await viewModel.SetOllamaEndpointAsync(endpoint.Text ?? "");
            status.Text = viewModel.ConnectionStatus;
            save.IsEnabled = true;
            if (connected) dialog.Close();
        };
        await dialog.ShowDialog(this);
    }

    private async void ConfigureCloudProvider_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var provider = new ComboBox { ItemsSource = new[] { "OpenAI", "Anthropic" }, SelectedIndex = 0, MinWidth = 180 };
        var apiKey = new TextBox { MinWidth = 360, PasswordChar = '•', Watermark = "Paste a key to replace the saved one (or leave blank)" };
        var acknowledgement = new CheckBox
        {
            Content = "I understand that prompts and conversation history go to this provider under its data policies, and API use may incur separate charges. Project files stay local unless I separately opt in below.",
            IsChecked = viewModel.CloudRequestsEnabled,
            MaxWidth = 390
        };
        var includeProjectContext = new CheckBox
        {
            Content = "Include attached project files in hosted prompts (otherwise they stay local)",
            IsChecked = viewModel.IncludeProjectContextForHosted,
            MaxWidth = 390
        };
        var status = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        var connect = new Button { Content = "Connect and load models", MinWidth = 170 };
        var forget = new Button { Content = "Remove saved key", MinWidth = 130 };
        var disable = new Button { Content = "Disable this session", MinWidth = 140 };
        var cancel = new Button { Content = "Close", MinWidth = 80 };
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { forget, disable, cancel, connect }
        };
        var dialog = new Window
        {
            Title = "Connect hosted models",
            Width = 660,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Choose a provider" },
                    provider,
                    new TextBlock { Text = "API key · saved in the OS credential store after a successful connection" },
                    apiKey,
                    new TextBlock
                    {
                        Text = "Leave the field blank to use the provider's environment variable first, then its saved key. Typed keys are saved after model discovery succeeds using Windows Credential Manager, macOS Keychain, or Linux Secret Service. They are never written to Codev settings, chats, or backups.",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        MaxWidth = 600
                    },
                    acknowledgement,
                    includeProjectContext,
                    status,
                    buttons
                }
            }
        };
        connect.Click += async (_, _) =>
        {
            var providerId = provider.SelectedItem?.ToString() == "Anthropic" ? Codev.CloudModelProviders.Anthropic : Codev.CloudModelProviders.OpenAI;
            var connected = await viewModel.ConnectCloudProviderAsync(providerId, apiKey.Text, acknowledgement.IsChecked == true);
            status.Text = viewModel.ConnectionStatus;
            if (connected)
            {
                viewModel.IncludeProjectContextForHosted = includeProjectContext.IsChecked == true;
                dialog.Close();
            }
        };
        disable.Click += (_, _) => { viewModel.DisableCloudProviders(); dialog.Close(); };
        forget.Click += async (_, _) =>
        {
            var providerId = provider.SelectedItem?.ToString() == "Anthropic" ? Codev.CloudModelProviders.Anthropic : Codev.CloudModelProviders.OpenAI;
            await viewModel.RemoveStoredCloudApiKeyAsync(providerId);
            apiKey.Text = "";
            status.Text = viewModel.ConnectionStatus;
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void ReadingWidth_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out var width) && DataContext is ViewModels.MainViewModel viewModel)
            viewModel.SetReadingWidth(width);
    }

    private async Task<string?> EditConversationPromptAsync(int messageIndex, string original)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.ActiveConversation is not { } conversation ||
            messageIndex < 0 || messageIndex >= conversation.Messages.Count || !conversation.Messages[messageIndex].IsUser) return null;
        var input = new TextBox
        {
            Text = original,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 180,
            MaxHeight = 420,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Padding = new Thickness(10),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch
        };
        var dialog = new Window
        {
            Title = "Edit prompt",
            Width = 720,
            Height = 430,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.FindResource("AppBackgroundBrush") as global::Avalonia.Media.IBrush,
            Content = new Grid
            {
                Margin = new Thickness(18),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 12,
                Children =
                {
                    new TextBlock { Text = "Revise the prompt. Confirming removes this prompt and all later conversation messages; project files are unchanged.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                    input,
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", Classes = { "soft" }, MinWidth = 90 },
                            new Button { Content = "Apply edit", MinWidth = 100, Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#D97757")), Foreground = global::Avalonia.Media.Brushes.White, BorderThickness = new Thickness(0) }
                        }
                    }
                }
            }
        };
        var layout = (Grid)dialog.Content!;
        Grid.SetRow(input, 1);
        var buttons = (StackPanel)layout.Children[2];
        Grid.SetRow(buttons, 2);
        var cancel = (Button)buttons.Children[0];
        var apply = (Button)buttons.Children[1];
        string? result = null;
        void UpdateApplyState() => apply.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => UpdateApplyState();
        UpdateApplyState();
        cancel.Click += (_, _) => dialog.Close();
        apply.Click += (_, _) =>
        {
            result = input.Text?.Trim();
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return result;
    }

    private async void AdvancedModelSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.ActiveConversation is not { } conversation) return;
        var primaryLabelBrush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(viewModel.IsDarkTheme ? "#ECECEC" : "#262522"));
        var secondaryLabelBrush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(viewModel.IsDarkTheme ? "#B0B0B0" : "#65625D"));
        var hints = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["temperature"] = "0–2 · Lower values make replies more predictable; higher values add variety.",
            ["top_p"] = "0–1 · Limits choices to the most likely group of tokens; lower values narrow that group.",
            ["top_k"] = "1–1000 · Limits each next-token choice to this many likely candidates.",
            ["presence_penalty"] = "0–2 · Discourages topics or tokens already used; high values can hurt code that repeats names.",
            ["repeat_penalty"] = "0–2 · Discourages repeating recent text; high values can make code inconsistent.",
            ["num_predict"] = "1–131072 · Caps the combined reasoning and answer tokens; a higher cap can take longer and use more memory."
        };
        var fields = new Dictionary<string, TextBox>(StringComparer.Ordinal)
        {
            ["temperature"] = SettingField(hints["temperature"], conversation.Temperature),
            ["top_p"] = SettingField(hints["top_p"], conversation.TopP),
            ["top_k"] = SettingField(hints["top_k"], conversation.TopK),
            ["presence_penalty"] = SettingField(hints["presence_penalty"], conversation.PresencePenalty),
            ["repeat_penalty"] = SettingField(hints["repeat_penalty"], conversation.RepeatPenalty),
            ["num_predict"] = SettingField(hints["num_predict"], conversation.NumPredict)
        };
        var presetPicker = new ComboBox { MinWidth = 180, ItemsSource = viewModel.SamplingPresets.Select(preset => preset.Name).ToArray() };
        var emptyPresetsNotice = new TextBlock
        {
            Text = "No saved presets yet. Use Save / update… to save these settings, or Import… to add a preset.",
            Foreground = secondaryLabelBrush,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 470
        };
        var presetStatus = new TextBlock
        {
            Text = viewModel.SamplingPresets.Count == 0 ? "" : $"{viewModel.SamplingPresets.Count} saved preset(s)",
            Foreground = secondaryLabelBrush,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };
        void RefreshPresets(string? selectName = null)
        {
            presetPicker.ItemsSource = viewModel.SamplingPresets.Select(preset => preset.Name).ToArray();
            presetPicker.IsVisible = viewModel.SamplingPresets.Count > 0;
            emptyPresetsNotice.IsVisible = viewModel.SamplingPresets.Count == 0;
            presetPicker.SelectedIndex = -1;
            if (selectName is not null) presetPicker.SelectedItem = selectName;
            presetStatus.Text = viewModel.SamplingPresets.Count == 0 ? "" : $"{viewModel.SamplingPresets.Count} saved preset(s)";
        }
        var applyPreset = new Button { Content = "Apply", Classes = { "soft" }, IsEnabled = false };
        var savePreset = new Button { Content = "Save / update…", Classes = { "soft" } };
        var removePreset = new Button { Content = "Remove", Classes = { "soft" }, IsEnabled = false };
        var importPreset = new Button { Content = "Import…", Classes = { "soft" } };
        var exportPreset = new Button { Content = "Export…", Classes = { "soft" }, IsEnabled = false };
        presetPicker.SelectionChanged += (_, _) =>
        {
            var selected = presetPicker.SelectedItem is string name && viewModel.SamplingPresets.FirstOrDefault(preset => preset.Name == name) is not null;
            applyPreset.IsEnabled = selected;
            removePreset.IsEnabled = selected;
            exportPreset.IsEnabled = selected;
        };
        applyPreset.Click += (_, _) =>
        {
            if (presetPicker.SelectedItem is not string name || viewModel.SamplingPresets.FirstOrDefault(preset => preset.Name == name) is not { } preset) return;
            fields["temperature"].Text = FormatPresetValue(preset.Temperature);
            fields["top_p"].Text = FormatPresetValue(preset.TopP);
            fields["top_k"].Text = preset.TopK?.ToString(CultureInfo.InvariantCulture) ?? "";
            fields["presence_penalty"].Text = FormatPresetValue(preset.PresencePenalty);
            fields["repeat_penalty"].Text = FormatPresetValue(preset.RepeatPenalty);
            fields["num_predict"].Text = preset.NumPredict?.ToString(CultureInfo.InvariantCulture) ?? "";
            presetStatus.Text = $"Loaded '{preset.Name}' into the fields. Press Save to apply it to this conversation.";
        };
        savePreset.Click += async (_, _) =>
        {
            if (!TryReadDouble(fields["temperature"], 0, 2, out var temperature) ||
                !TryReadDouble(fields["top_p"], 0, 1, out var topP) ||
                !TryReadInt(fields["top_k"], 1, 1000, out var topK) ||
                !TryReadDouble(fields["presence_penalty"], 0, 2, out var presencePenalty) ||
                !TryReadDouble(fields["repeat_penalty"], 0, 2, out var repeatPenalty) ||
                !TryReadInt(fields["num_predict"], 1, 131072, out var numPredict) ||
                !(temperature.HasValue || topP.HasValue || topK.HasValue || presencePenalty.HasValue || repeatPenalty.HasValue || numPredict.HasValue))
            {
                presetStatus.Text = "Enter at least one valid setting before saving a preset; leave other fields blank for model defaults.";
                return;
            }
            var name = await PromptSamplingPresetNameAsync(dialogOwner: this, presetPicker.SelectedItem as string);
            if (name is null) return;
            var preset = new Codev.SamplingPreset(name, temperature, topP, topK, presencePenalty, repeatPenalty, numPredict);
            if (!Codev.SamplingPresetCatalog.TryNormalize(preset, out preset))
            {
                presetStatus.Text = $"Preset names must be 1–{Codev.SamplingPresetCatalog.MaxNameLength} characters.";
                return;
            }
            var presets = viewModel.SamplingPresets.ToList();
            var index = presets.FindIndex(existing => existing.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && presets.Count >= Codev.SamplingPresetCatalog.MaxPresets)
            {
                presetStatus.Text = $"You can save up to {Codev.SamplingPresetCatalog.MaxPresets} presets.";
                return;
            }
            if (index < 0) presets.Add(preset); else presets[index] = preset;
            viewModel.SaveSamplingPresets(presets);
            RefreshPresets(preset.Name);
        };
        removePreset.Click += (_, _) =>
        {
            if (presetPicker.SelectedItem is not string name) return;
            viewModel.SaveSamplingPresets(viewModel.SamplingPresets.Where(preset => preset.Name != name));
            RefreshPresets();
        };
        importPreset.Click += async (_, _) =>
        {
            if (!StorageProvider.CanOpen) { presetStatus.Text = "This platform does not provide a local file picker."; return; }
            try
            {
                var filesToOpen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import sampling preset",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Codev sampling preset") { Patterns = ["*.codev-preset.json", "*.json"] }]
                });
                var file = filesToOpen.FirstOrDefault();
                if (file is null) return;
                var preset = Codev.SamplingPresetCatalog.Deserialize(await ReadTextFileAsync(file, 32_000));
                var presets = viewModel.SamplingPresets.ToList();
                var index = presets.FindIndex(existing => existing.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
                if (index < 0 && presets.Count >= Codev.SamplingPresetCatalog.MaxPresets)
                {
                    presetStatus.Text = $"You can save up to {Codev.SamplingPresetCatalog.MaxPresets} presets. Remove one before importing.";
                    return;
                }
                if (index < 0) presets.Add(preset); else presets[index] = preset;
                viewModel.SaveSamplingPresets(presets);
                RefreshPresets(preset.Name);
                presetStatus.Text = $"Imported '{preset.Name}'. Apply it to load the fields, then press Save to use it.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { presetStatus.Text = $"Could not import preset: {ex.Message}"; }
        };
        exportPreset.Click += async (_, _) =>
        {
            if (presetPicker.SelectedItem is not string name || viewModel.SamplingPresets.FirstOrDefault(preset => preset.Name == name) is not { } preset) return;
            if (!StorageProvider.CanSave) { presetStatus.Text = "This platform does not provide a local save dialog."; return; }
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export sampling preset",
                    SuggestedFileName = $"{SafeExportName(preset.Name)}.codev-preset.json",
                    DefaultExtension = "json",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType("Codev sampling preset") { Patterns = ["*.codev-preset.json", "*.json"] }]
                });
                if (file is null) return;
                await WriteTextFileAsync(file, Codev.SamplingPresetCatalog.Serialize(preset));
                presetStatus.Text = $"Exported '{preset.Name}' · {file.Name}";
            }
            catch (Exception ex) { presetStatus.Text = $"Could not export preset: {ex.Message}"; }
        };
        string FormatPresetValue(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Blank means the model's default. Values are saved with this conversation and captured for queued turns. Maximum tokens limits the combined reasoning and answer output; a higher limit can take longer and use more memory. Loading defaults reads this model's metadata from the configured Ollama server; unlisted values may be runtime defaults.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 470,
            Foreground = secondaryLabelBrush
        });
        var loadDefaults = new Button { Content = "Load model defaults…", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left };
        var status = new TextBlock { Foreground = secondaryLabelBrush, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        panel.Children.Add(new TextBlock { Text = "Your sampling presets", Foreground = primaryLabelBrush, FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(presetPicker);
        panel.Children.Add(emptyPresetsNotice);
        panel.Children.Add(new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { applyPreset, savePreset, removePreset } });
        panel.Children.Add(new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { importPreset, exportPreset } });
        panel.Children.Add(presetStatus);
        panel.Children.Add(loadDefaults);
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock
        {
            Text = "Sampling parameters",
            Foreground = primaryLabelBrush,
            FontSize = 13,
            FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 2)
        });
        foreach (var (name, field) in fields)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = primaryLabelBrush,
                FontSize = 12,
                FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            });
            var useDefault = new Button { Content = "Default", Classes = { "soft" }, Padding = new Thickness(7, 2), FontSize = 10 };
            useDefault.Click += (_, _) => field.Text = "";
            global::Avalonia.Controls.Grid.SetColumn(useDefault, 1);
            header.Children.Add(useDefault);
            panel.Children.Add(header);
            panel.Children.Add(new TextBlock
            {
                Text = hints[name],
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                Foreground = secondaryLabelBrush,
                MaxWidth = 470,
                FontSize = 11
            });
            global::Avalonia.Automation.AutomationProperties.SetName(field, name);
            global::Avalonia.Controls.ToolTip.SetTip(useDefault, "Clear this override and use the model default");
            panel.Children.Add(field);
        }
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var resolvedBackground = (this.FindResource("AppBackgroundBrush") as global::Avalonia.Media.ISolidColorBrush)?.Color
            ?? global::Avalonia.Media.Color.Parse("#191919");
        var opaqueBackground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(
            resolvedBackground.R, resolvedBackground.G, resolvedBackground.B));
        panel.Background = opaqueBackground;
        var dialog = new Window
        {
            Title = "Advanced model settings",
            Width = 520,
            Height = 700,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = opaqueBackground,
            Foreground = primaryLabelBrush,
            Content = new Border
            {
                Background = opaqueBackground,
                Child = new ScrollViewer
                {
                    Background = opaqueBackground,
                    Content = panel,
                    VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                }
            }
        };
        loadDefaults.Click += async (_, _) =>
        {
            loadDefaults.IsEnabled = false;
            status.Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush;
            status.Text = "Loading declared settings from Ollama…";
            try
            {
                var defaults = await viewModel.LoadDeclaredModelDefaultsAsync(conversation.Model);
                foreach (var (name, field) in fields)
                {
                    var declared = defaults.TryGetValue(name, out var value)
                        ? $"Declared by model: {value.Trim().Trim('"')}"
                        : "Not declared by model · Ollama runtime default";
                    field.Watermark = $"{declared} · {hints[name]}";
                }
                status.Text = defaults.Count == 0
                    ? "Ollama returned no declared parameters. Blank fields still use Ollama's defaults."
                    : "Showing settings declared by this model. Blank fields continue to use the effective Ollama defaults.";
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
            {
                status.Text = $"Could not load model metadata ({ex.GetType().Name}). Check that Ollama is running and the model is installed; blank fields still use the model defaults.";
            }
            finally { loadDefaults.IsEnabled = true; }
        };
        var reset = new Button { Content = "Reset all to model defaults" };
        reset.Click += (_, _) =>
        {
            viewModel.SetSamplingSettings(null, null, null, null, null, null);
            dialog.Close();
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (!TryReadDouble(fields["temperature"], 0, 2, out var temperature) ||
                !TryReadDouble(fields["top_p"], 0, 1, out var topP) ||
                !TryReadInt(fields["top_k"], 1, 1000, out var topK) ||
                !TryReadDouble(fields["presence_penalty"], 0, 2, out var presencePenalty) ||
                !TryReadDouble(fields["repeat_penalty"], 0, 2, out var repeatPenalty) ||
                !TryReadInt(fields["num_predict"], 1, 131072, out var numPredict))
            {
                status.Foreground = global::Avalonia.Media.Brushes.IndianRed;
                status.Text = "Enter a number within the range shown for each setting, or leave it blank to use the model default.";
                return;
            }
            viewModel.SetSamplingSettings(temperature, topP, topK, presencePenalty, repeatPenalty, numPredict);
            dialog.Close();
        };
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        await dialog.ShowDialog(this);

        static TextBox SettingField(string hint, object? value) => new()
        {
            Watermark = $"Model default · {hint}",
            Text = value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture),
            MinWidth = 280
        };

        static bool TryReadDouble(TextBox field, double min, double max, out double? value)
        {
            var text = field.Text?.Trim();
            if (string.IsNullOrEmpty(text)) { value = null; return true; }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed >= min && parsed <= max)
            { value = parsed; return true; }
            value = null;
            return false;
        }

        static bool TryReadInt(TextBox field, int min, int max, out int? value)
        {
            var text = field.Text?.Trim();
            if (string.IsNullOrEmpty(text)) { value = null; return true; }
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max)
            { value = parsed; return true; }
            value = null;
            return false;
        }
    }

    private static async Task<string?> PromptSamplingPresetNameAsync(Window dialogOwner, string? currentName)
    {
        var nameBox = new TextBox
        {
            Text = currentName ?? "",
            MaxLength = Codev.SamplingPresetCatalog.MaxNameLength,
            Watermark = "e.g. Focused coding",
            MinWidth = 300
        };
        var error = new TextBlock
        {
            Text = $"Choose a name of 1–{Codev.SamplingPresetCatalog.MaxNameLength} characters.",
            IsVisible = false,
            Foreground = global::Avalonia.Media.Brushes.IndianRed
        };
        var save = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, save }
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Preset name" },
                nameBox,
                error,
                buttons
            }
        };
        var dialog = new Window
        {
            Title = currentName is null ? "Save sampling preset" : "Update sampling preset",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = dialogOwner.FindResource("AppBackgroundBrush") as global::Avalonia.Media.IBrush,
            Foreground = dialogOwner.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush,
            Content = panel
        };
        cancel.Click += (_, _) => dialog.Close(null);
        save.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > Codev.SamplingPresetCatalog.MaxNameLength)
            {
                error.IsVisible = true;
                return;
            }
            dialog.Close(name);
        };
        dialog.Opened += (_, _) => nameBox.Focus();
        return await dialog.ShowDialog<string?>(dialogOwner);
    }

    private async void LoadedModels_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var list = new StackPanel { Spacing = 8 };
        var status = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        var refresh = new Button { Content = "Refresh", Classes = { "soft" }, MinWidth = 90 };
        var close = new Button { Content = "Close", Classes = { "soft" }, MinWidth = 80 };
        var dialog = new Window
        {
            Title = "Models loaded in Ollama",
            Width = 700,
            Height = 560,
            MinWidth = 520,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { refresh, close }
        };
        var content = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 10 };
        content.Children.Add(new TextBlock { Text = $"Ollama server · {viewModel.OllamaEndpointDisplay}", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        var note = new TextBlock
        {
            Text = "Ollama-reported model size and VRAM allocation. These server statistics are not a complete system RAM meter.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
        };
        Grid.SetRow(note, 1);
        content.Children.Add(note);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        content.Children.Add(scroll);
        var footer = new StackPanel { Spacing = 8, Children = { status, buttons } };
        Grid.SetRow(footer, 3);
        content.Children.Add(footer);
        dialog.Content = content;
        close.Click += (_, _) => dialog.Close();

        static string MemorySize(long bytes) => bytes <= 0 ? "0 B" : bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.00} GiB" : $"{bytes / (1024d * 1024):0} MiB";

        async Task RefreshListAsync()
        {
            refresh.IsEnabled = false;
            status.Text = "Refreshing loaded-model state…";
            list.Children.Clear();
            try
            {
                var loaded = await viewModel.GetLoadedModelsAsync();
                if (loaded.Count == 0)
                    list.Children.Add(new TextBlock { Text = "No models are currently loaded.", Margin = new Thickness(4, 10) });
                foreach (var model in loaded)
                {
                    var details = new StackPanel { Spacing = 4 };
                    details.Children.Add(new TextBlock { Text = model.Name, FontWeight = global::Avalonia.Media.FontWeight.SemiBold, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
                    details.Children.Add(new TextBlock
                    {
                        Text = $"Model size: {MemorySize(model.Size)}    VRAM: {MemorySize(model.SizeVram)}" +
                               (model.ContextLength > 0 ? $"    Context: {model.ContextLength:N0}" : "") +
                               (model.ExpiresAt is { } expiry ? $"\nExpires: {expiry.ToLocalTime():g}" : ""),
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush,
                        FontSize = 11
                    });
                    var unload = new Button { Content = "Unload", Classes = { "soft" }, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
                    unload.Click += async (_, _) =>
                    {
                        if (!await ConfirmGitActionAsync(dialog, "Unload model?", $"Unload {model.Name} from the Ollama server and release its allocated model memory? The model remains installed and can be loaded again by selecting it.")) return;
                        unload.IsEnabled = false;
                        status.Text = $"Unloading {model.Name}…";
                        try
                        {
                            if (await viewModel.UnloadModelAsync(model.Name))
                                await RefreshListAsync();
                            else
                                status.Text = string.IsNullOrWhiteSpace(viewModel.ContextActionStatus) ? viewModel.ConnectionStatus : viewModel.ContextActionStatus;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException)
                        {
                            status.Text = $"Could not unload model: {ex.Message}";
                            unload.IsEnabled = true;
                        }
                    };
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
                    row.Children.Add(details);
                    Grid.SetColumn(unload, 1);
                    row.Children.Add(unload);
                    list.Children.Add(new Border
                    {
                        Background = this.FindResource("SurfaceBrush") as global::Avalonia.Media.IBrush,
                        BorderBrush = this.FindResource("FieldBorderBrush") as global::Avalonia.Media.IBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12),
                        Child = row
                    });
                }
                status.Text = loaded.Count == 0 ? "Loaded state refreshed." : $"{loaded.Count} model(s) loaded.";
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException)
            {
                status.Text = $"Could not read loaded models: {ex.Message}";
            }
            finally { refresh.IsEnabled = true; }
        }

        refresh.Click += async (_, _) => await RefreshListAsync();
        _ = RefreshListAsync();
        await dialog.ShowDialog(this);
    }

    private async void Composer_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _slashCommandSearch?.Cancel();
        _slashCommandSearch?.Dispose();
        _slashCommandSearch = null;
        _fileMentionSearch?.Cancel();
        _fileMentionSearch?.Dispose();
        _fileMentionSearch = null;
        var text = ComposerTextBox.Text ?? "";
        if (DataContext is not ViewModels.MainViewModel viewModel)
        {
            SlashCommandPopup.IsOpen = false;
            FileMentionPopup.IsOpen = false;
            _activeFileMention = null;
            return;
        }

        if (Codev.SlashCommandCatalog.TryGetCommandToken(text, ComposerTextBox.CaretIndex, out _, out _))
        {
            FileMentionPopup.IsOpen = false;
            _activeFileMention = null;
            var slashSearch = _slashCommandSearch = new CancellationTokenSource();
            var slashCaretIndex = ComposerTextBox.CaretIndex;
            try
            {
                await Task.Delay(100, slashSearch.Token);
                var commands = await viewModel.GetSlashCommandSuggestionsAsync(text, slashCaretIndex, slashSearch.Token);
                if (slashSearch.IsCancellationRequested || !ReferenceEquals(DataContext, viewModel) ||
                    ComposerTextBox.Text != text || ComposerTextBox.CaretIndex != slashCaretIndex) return;
                SlashCommandListBox.ItemsSource = commands;
                SlashCommandListBox.SelectedIndex = -1;
                SlashCommandPopup.IsOpen = commands.Count > 0;
            }
            catch (OperationCanceledException) { }
            return;
        }

        SlashCommandPopup.IsOpen = false;

        if (!Codev.ProjectFileMentionParser.TryGet(text, ComposerTextBox.CaretIndex, out var mention))
        {
            FileMentionPopup.IsOpen = false;
            _activeFileMention = null;
            return;
        }

        var search = _fileMentionSearch = new CancellationTokenSource();
        var caretIndex = ComposerTextBox.CaretIndex;
        try
        {
            await Task.Delay(120, search.Token);
            var suggestions = await Task.Run(() => viewModel.GetProjectFileSuggestions(mention.Prefix), search.Token);
            if (search.IsCancellationRequested || !ReferenceEquals(DataContext, viewModel) || ComposerTextBox.Text != text || ComposerTextBox.CaretIndex != caretIndex) return;
            FileMentionListBox.ItemsSource = suggestions;
            FileMentionListBox.SelectedIndex = -1;
            _activeFileMention = suggestions.Count > 0 ? mention : null;
            FileMentionPopup.IsOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException) { }
    }

    private void SlashCommandSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Codev.SlashCommandDefinition command }) _ = ApplySlashCommandAsync(command);
    }

    private async Task ApplySlashCommandAsync(Codev.SlashCommandDefinition command)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel ||
            SlashCommandListBox.Items?.OfType<Codev.SlashCommandDefinition>().Contains(command) != true)
            return;

        SlashCommandPopup.IsOpen = false;
        FileMentionPopup.IsOpen = false;
        _activeFileMention = null;
        switch (command.Action)
        {
            case Codev.SlashCommandAction.CompactConversation:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                await CompactConversationAsync(viewModel);
                break;
            case Codev.SlashCommandAction.ClearConversation:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (!viewModel.CanClearConversation)
                {
                    viewModel.ReportContextActionStatus("Wait for this conversation to finish and clear its queued requests before clearing its history.");
                    return;
                }
                if (await ConfirmGitActionAsync(this, "Clear conversation?", "This removes this conversation's saved messages, draft, and unsent diff comments. Project selection, model settings, and file-change history will remain."))
                    viewModel.ClearActiveConversationHistory();
                break;
            case Codev.SlashCommandAction.TogglePlan:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (viewModel.TogglePlanModeCommand.CanExecute(null)) viewModel.TogglePlanModeCommand.Execute(null);
                else viewModel.ReportContextActionStatus("Wait for the current response to finish before changing conversation mode.");
                break;
            case Codev.SlashCommandAction.ToggleCodeTask:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (viewModel.ToggleCodeTaskCommand.CanExecute(null)) viewModel.ToggleCodeTaskCommand.Execute(null);
                else if (viewModel.GetCodeTaskUnavailableReason() is { } codeTaskReason) viewModel.ReportContextActionStatus(codeTaskReason);
                else viewModel.ReportContextActionStatus("Wait for the current response to finish before changing conversation mode.");
                break;
            case Codev.SlashCommandAction.ShowStatus:
                if (viewModel.PendingDiffComments.Count > 0)
                {
                    viewModel.ReportContextActionStatus("Send or remove the pending diff comments before running /status.");
                    return;
                }
                viewModel.Draft = "/status";
                ComposerTextBox.Text = "/status";
                if (viewModel.SendCommand.CanExecute(null)) viewModel.SendCommand.Execute(null);
                break;
            case Codev.SlashCommandAction.SelectModel:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (ModelPicker.IsVisible) { ModelPicker.Focus(); ModelPicker.IsDropDownOpen = true; }
                else viewModel.ReportContextActionStatus("Model choices are still loading or unavailable.");
                break;
            case Codev.SlashCommandAction.ExportConversation:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                ExportConversation_Click(this, new RoutedEventArgs());
                break;
            case Codev.SlashCommandAction.OpenCommandsFolder:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                await ShowSlashCommandFoldersAsync(viewModel);
                break;
            case Codev.SlashCommandAction.OpenSkillsFolder:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                await ShowSkillFoldersAsync(viewModel);
                break;
            case Codev.SlashCommandAction.InitProject:
                if (!viewModel.IsCodeTask && !viewModel.IsGenerating && viewModel.CanEnterCodeTaskMode)
                {
                    if (viewModel.IsPlanMode && viewModel.TogglePlanModeCommand.CanExecute(null))
                        viewModel.TogglePlanModeCommand.Execute(null);
                    if (viewModel.ToggleCodeTaskCommand.CanExecute(null))
                    {
                        viewModel.ToggleCodeTaskCommand.Execute(null);
                        viewModel.ReportContextActionStatus("Code task mode selected. Review the /init prompt, then send it to inspect the trusted project; any AGENTS.md draft will require your approval.");
                    }
                }
                else if (!viewModel.IsCodeTask)
                {
                    viewModel.ReportContextActionStatus("/init can still draft guidance in Chat mode. To let the model inspect project files and propose AGENTS.md, use a trusted project with a local Ollama model.");
                }
                goto case Codev.SlashCommandAction.ReviewProject;
            case Codev.SlashCommandAction.ReviewProject:
                viewModel.Draft = command.Prompt ?? "";
                ComposerTextBox.Text = viewModel.Draft;
                ComposerTextBox.CaretIndex = ComposerTextBox.Text?.Length ?? 0;
                ComposerTextBox.Focus();
                break;
            case Codev.SlashCommandAction.ReviewWorkingTree:
            case Codev.SlashCommandAction.SecurityReviewWorkingTree:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (await viewModel.ReviewUncommittedChangesAsync(securityFocused: command.Action == Codev.SlashCommandAction.SecurityReviewWorkingTree) is { } review)
                    await ShowReadOnlyReviewAsync(review, this);
                break;
            case Codev.SlashCommandAction.UserPrompt:
                if (command.ArgumentNames is { Count: > 0 } argumentNames &&
                    Codev.SlashCommandCatalog.TryGetCommandToken(ComposerTextBox.Text, ComposerTextBox.CaretIndex, out var token, out var hasArguments) &&
                    !hasArguments && token.Equals(command.Name, StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.Draft = command.Name + " ";
                    ComposerTextBox.Text = viewModel.Draft;
                    ComposerTextBox.CaretIndex = ComposerTextBox.Text.Length;
                    ComposerTextBox.Focus();
                    viewModel.ReportContextActionStatus($"Fill the named arguments: {string.Join(" ", argumentNames.Select(argument => argument + "=<value>"))}. Quote values containing spaces.");
                    break;
                }
                var expansion = await viewModel.ExpandSlashCommandAsync(command, ComposerTextBox.Text ?? "");
                if (!expansion.Success)
                {
                    viewModel.ReportContextActionStatus(expansion.Error);
                    break;
                }
                viewModel.Draft = expansion.Prompt;
                ComposerTextBox.Text = expansion.Prompt;
                ComposerTextBox.CaretIndex = ComposerTextBox.Text.Length;
                ComposerTextBox.Focus();
                break;
        }
    }

    private async Task CompactConversationAsync(ViewModels.MainViewModel viewModel)
    {
        var proposal = await viewModel.CreateCompactionProposalAsync();
        if (proposal is null) return;
        await ShowCompactionProposalAsync(proposal);
    }

    private async Task ShowCompactionProposalAsync(Codev.ConversationCompactionProposal proposal)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var summary = new TextBox
        {
            Text = proposal.Summary,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            MinHeight = 280
        };
        var status = new TextBlock
        {
            Text = $"This summarizes {proposal.CompactedTurns} earlier complete exchange(s), keeping the latest {proposal.KeptTurns} exchange(s) verbatim. The original transcript stays visible, saved, and in exports. Review or edit the summary before applying it.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
        };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" }, MinWidth = 84 };
        var apply = new Button { Content = "Apply summary", MinWidth = 120 };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, apply } };
        var dialog = new Window
        {
            Title = "Review conversation summary",
            Width = 720,
            Height = 540,
            MinWidth = 520,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Margin = new Thickness(18),
                RowSpacing = 12,
                Children = { status, summary, buttons }
            }
        };
        Grid.SetRow(summary, 1);
        Grid.SetRow(buttons, 2);
        cancel.Click += (_, _) => dialog.Close();
        apply.Click += (_, _) =>
        {
            if (viewModel.ApplyCompactionProposal(proposal, summary.Text ?? "")) dialog.Close();
            else status.Text = viewModel.ContextActionStatus;
        };
        await dialog.ShowDialog(this);
    }

    private async void CompactConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) await CompactConversationAsync(viewModel);
    }

    private void RestoreFullHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) viewModel.ClearActiveCompaction();
    }

    private async Task ShowSlashCommandFoldersAsync(ViewModels.MainViewModel viewModel)
    {
        var projectFolder = viewModel.ProjectSlashCommandsFolder;
        var dialog = new Window
        {
            Title = "Custom slash commands",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Add a .md file to either folder. Its filename becomes the slash command, for example inspect.md creates /inspect. User commands work in every chat; project commands load only for an attached trusted project. Project commands override user commands with the same name. Markdown is treated as prompt text only; Codev never executes scripts from command files.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Saved prompt templates from the WPF app also appear as /template-… commands (SAVED) and insert into the composer for review. Manage those templates in the WPF app or migrate them to Markdown files here.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
        });
        content.Children.Add(new Border
        {
            Background = this.FindResource("SurfaceBrush") as global::Avalonia.Media.IBrush,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = "---\ndescription: Review a named area\narguments: area, file\n---\nReview {{area}} in {{file}}. Return actionable findings only.",
                FontFamily = "Cascadia Code",
                FontSize = 10,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
            }
        });
        content.Children.Add(new TextBlock { Text = "User commands · available in all conversations", FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(new TextBox { Text = viewModel.UserSlashCommandsFolder, IsReadOnly = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MinHeight = 44 });
        var openUser = new Button { Content = "Open user commands folder", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left };
        openUser.Click += async (_, _) => await OpenCommandFolderAsync(viewModel.UserSlashCommandsFolder, viewModel);
        content.Children.Add(openUser);
        content.Children.Add(new TextBlock { Text = "Project commands · trusted project only", FontWeight = global::Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 5, 0, 0) });
        content.Children.Add(new TextBox
        {
            Text = projectFolder ?? (viewModel.HasProject ? "Trust this project to enable its .codev/commands folder." : "Attach and trust a project to use project commands."),
            IsReadOnly = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 44
        });
        var openProject = new Button { Content = "Open project commands folder", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left, IsEnabled = projectFolder is not null };
        if (projectFolder is not null) openProject.Click += async (_, _) => await OpenCommandFolderAsync(projectFolder, viewModel);
        content.Children.Add(openProject);
        var close = new Button { Content = "Close", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        content.Children.Add(close);
        dialog.Content = content;
        await dialog.ShowDialog(this);
    }

    private async Task ShowSkillFoldersAsync(ViewModels.MainViewModel viewModel)
    {
        var projectFolder = viewModel.ProjectSkillsFolder;
        var dialog = new Window
        {
            Title = "Markdown skills",
            Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "A skill is a folder containing SKILL.md. It appears as /skill-name with its description in the menu. Selecting it loads the prompt into the composer for review; it is never sent automatically. Skills are manually invoked only, and Codev does not run scripts from skill folders or let the model trigger them.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new Border
        {
            Background = this.FindResource("SurfaceBrush") as global::Avalonia.Media.IBrush,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = "review/SKILL.md\n---\ndescription: Review a project for correctness and test gaps\narguments: area\n---\nReview {{area}}. Return prioritized, actionable findings only.",
                FontFamily = "Cascadia Code",
                FontSize = 10,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
            }
        });
        content.Children.Add(new TextBlock { Text = "User skills · available in all conversations", FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(new TextBox { Text = viewModel.UserSkillsFolder, IsReadOnly = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MinHeight = 44 });
        var openUser = new Button { Content = "Open user skills folder", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left };
        openUser.Click += async (_, _) => await OpenCommandFolderAsync(viewModel.UserSkillsFolder, viewModel);
        content.Children.Add(openUser);
        content.Children.Add(new TextBlock { Text = "Project skills · trusted project only", FontWeight = global::Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 5, 0, 0) });
        content.Children.Add(new TextBox
        {
            Text = projectFolder ?? (viewModel.HasProject ? "Trust this project to enable its .codev/skills folder." : "Attach and trust a project to use project skills."),
            IsReadOnly = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 44
        });
        var openProject = new Button { Content = "Open project skills folder", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left, IsEnabled = projectFolder is not null };
        if (projectFolder is not null) openProject.Click += async (_, _) => await OpenCommandFolderAsync(projectFolder, viewModel);
        content.Children.Add(openProject);
        var close = new Button { Content = "Close", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        content.Children.Add(close);
        dialog.Content = content;
        await dialog.ShowDialog(this);
    }

    private async void PromptTemplates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var templates = viewModel.PromptTemplates.ToList();
        var list = new ListBox { MinHeight = 220, ItemsSource = templates.Select(item => item.Name).ToArray() };
        var preview = new TextBlock
        {
            Text = "Select a template to preview its prompt.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxHeight = 130,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top
        };
        var managerStatus = new TextBlock { Text = $"{templates.Count} of {Codev.PromptTemplateCatalog.MaxTemplates} templates", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        list.SelectionChanged += (_, _) =>
        {
            var selected = templates.FirstOrDefault(item => item.Name == list.SelectedItem as string);
            preview.Text = selected?.Prompt ?? "Select a template to preview its prompt.";
        };
        var add = new Button { Content = "Add…", Classes = { "soft" } };
        var edit = new Button { Content = "Edit…", Classes = { "soft" } };
        var remove = new Button { Content = "Remove", Classes = { "soft" } };
        var use = new Button { Content = "Use in composer", Classes = { "soft" } };
        var close = new Button { Content = "Close", Classes = { "soft" } };
        var dialog = new Window
        {
            Title = "Prompt templates",
            Width = 620,
            Height = 540,
            MinWidth = 500,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };
        void RefreshList(string? selectedName = null)
        {
            list.ItemsSource = templates.Select(item => item.Name).ToArray();
            list.SelectedIndex = -1;
            if (selectedName is not null) list.SelectedItem = selectedName;
            else preview.Text = "Select a template to preview its prompt.";
            managerStatus.Text = $"{templates.Count} of {Codev.PromptTemplateCatalog.MaxTemplates} templates";
        }
        add.Click += async (_, _) =>
        {
            if (templates.Count >= Codev.PromptTemplateCatalog.MaxTemplates)
            {
                managerStatus.Text = $"You can save up to {Codev.PromptTemplateCatalog.MaxTemplates} templates.";
                return;
            }
            var template = await EditPromptTemplateAsync(dialog, null);
            if (template is null) return;
            if (templates.Any(item => item.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase)))
            {
                managerStatus.Text = "Template names must be unique.";
                return;
            }
            templates.Add(template);
            viewModel.SavePromptTemplates(templates);
            RefreshList(template.Name);
        };
        edit.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string selectedName) return;
            var index = templates.FindIndex(item => item.Name == selectedName);
            if (index < 0) return;
            var edited = await EditPromptTemplateAsync(dialog, templates[index]);
            if (edited is null) return;
            if (templates.Where((_, itemIndex) => itemIndex != index).Any(item => item.Name.Equals(edited.Name, StringComparison.OrdinalIgnoreCase)))
            {
                managerStatus.Text = "Template names must be unique.";
                return;
            }
            templates[index] = edited;
            viewModel.SavePromptTemplates(templates);
            RefreshList(edited.Name);
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not string selectedName) return;
            templates.RemoveAll(item => item.Name == selectedName);
            viewModel.SavePromptTemplates(templates);
            RefreshList();
        };
        use.Click += (_, _) =>
        {
            if (list.SelectedItem is not string selectedName) return;
            var template = templates.FirstOrDefault(item => item.Name == selectedName);
            if (template is null) return;
            var prefix = string.IsNullOrWhiteSpace(viewModel.Draft) ? "" : viewModel.Draft.TrimEnd() + Environment.NewLine + Environment.NewLine;
            viewModel.Draft = prefix + template.Prompt;
            ComposerTextBox.Text = viewModel.Draft;
            ComposerTextBox.CaretIndex = ComposerTextBox.Text.Length;
            ComposerTextBox.Focus();
            dialog.Close();
        };
        close.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { add, edit, remove, use, close }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Reusable prompts stay on this device. Choose Use to place a prompt in the composer for review before sending.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                list,
                new ScrollViewer { Content = preview, MaxHeight = 140, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
                managerStatus,
                buttons
            }
        };
        if (list.ItemCount > 0) list.SelectedIndex = 0;
        await dialog.ShowDialog(this);
    }

    private static async Task<Codev.PromptTemplate?> EditPromptTemplateAsync(Window owner, Codev.PromptTemplate? original)
    {
        var name = new TextBox { Text = original?.Name ?? "", MaxLength = Codev.PromptTemplateCatalog.MaxNameCharacters, Watermark = "Template name" };
        var prompt = new TextBox
        {
            Text = original?.Prompt ?? "",
            MaxLength = Codev.PromptTemplateCatalog.MaxPromptCharacters,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
            Watermark = "Prompt text"
        };
        var dialog = new Window
        {
            Title = original is null ? "Add prompt template" : "Edit prompt template",
            Width = 560,
            Height = 460,
            MinWidth = 440,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var status = new TextBlock { Text = "", Foreground = owner.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" } };
        var save = new Button { Content = "Save", Classes = { "soft" } };
        Codev.PromptTemplate? result = null;
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            var candidate = new Codev.PromptTemplate(name.Text?.Trim() ?? "", prompt.Text?.Trim() ?? "");
            var normalized = Codev.PromptTemplateCatalog.Normalize([candidate]);
            if (normalized.Count == 0)
            {
                status.Text = "Enter a name and prompt within the displayed length limits.";
                return;
            }
            result = normalized[0];
            dialog.Close();
        };
        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(18),
            RowSpacing = 10,
            Children =
            {
                name,
                prompt,
                status,
                new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
            }
        };
        Grid.SetRow(prompt, 1);
        Grid.SetRow(status, 2);
        Grid.SetRow((global::Avalonia.Controls.Control)((Grid)dialog.Content).Children[3], 3);
        await dialog.ShowDialog(owner);
        return result;
    }

    private static async Task OpenCommandFolderAsync(string path, ViewModels.MainViewModel viewModel)
    {
        try
        {
            Directory.CreateDirectory(path);
            var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            var start = new ProcessStartInfo(opener) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            viewModel.ReportContextActionStatus($"Could not open the command folder: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    private void FileMentionSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string path }) ApplyProjectFileMention(path);
    }

    private void ApplyProjectFileMention(string path)
    {
        var currentText = ComposerTextBox.Text ?? "";
        if (_activeFileMention is null || DataContext is not ViewModels.MainViewModel viewModel ||
            !Codev.ProjectFileMentionParser.TryGet(currentText, ComposerTextBox.CaretIndex, out var currentMention) ||
            currentMention.StartIndex != _activeFileMention.StartIndex ||
            (path.StartsWith("rule:", StringComparison.OrdinalIgnoreCase)
                ? !viewModel.CanMentionProjectRule(path)
                : !viewModel.AddProjectFileMention(path)))
        {
            FileMentionPopup.IsOpen = false;
            _activeFileMention = null;
            return;
        }
        var (text, caretIndex) = Codev.ProjectFileMentionParser.Insert(currentText, currentMention, path);
        FileMentionPopup.IsOpen = false;
        _activeFileMention = null;
        ComposerTextBox.Text = text;
        ComposerTextBox.CaretIndex = caretIndex;
        viewModel.Draft = text;
        ComposerTextBox.Focus();
    }

    private void Composer_KeyDown(object? sender, KeyEventArgs e)
    {
        if (SlashCommandPopup.IsOpen && e.Key is Key.Down or Key.Up)
        {
            var count = SlashCommandListBox.ItemCount;
            if (count > 0) SlashCommandListBox.SelectedIndex = Math.Clamp(SlashCommandListBox.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
            e.Handled = true;
            return;
        }
        if (SlashCommandPopup.IsOpen && (e.Key is Key.Enter or Key.Tab))
        {
            var selected = SlashCommandListBox.SelectedItem as Codev.SlashCommandDefinition ??
                (e.Key == Key.Enter ? SlashCommandListBox.Items?.OfType<Codev.SlashCommandDefinition>().FirstOrDefault() : null);
            if (selected is not null) _ = ApplySlashCommandAsync(selected);
            e.Handled = selected is not null;
            return;
        }
        if (SlashCommandPopup.IsOpen && e.Key == Key.Escape)
        {
            SlashCommandPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (FileMentionPopup.IsOpen && e.Key is Key.Down or Key.Up)
        {
            var count = FileMentionListBox.ItemCount;
            if (count > 0) FileMentionListBox.SelectedIndex = Math.Clamp(FileMentionListBox.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
            e.Handled = true;
            return;
        }
        if (FileMentionPopup.IsOpen && (e.Key is Key.Enter or Key.Tab))
        {
            var selectedPath = FileMentionListBox.SelectedItem as string ??
                (e.Key == Key.Enter ? FileMentionListBox.Items?.OfType<string>().FirstOrDefault() : null);
            if (selectedPath is not null) ApplyProjectFileMention(selectedPath);
            e.Handled = selectedPath is not null;
            return;
        }
        if (FileMentionPopup.IsOpen && e.Key == Key.Escape)
        {
            FileMentionPopup.IsOpen = false;
            _activeFileMention = null;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        viewModel.Draft = ComposerTextBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(viewModel.Draft)) return;
        if (viewModel.SendCommand.CanExecute(null)) viewModel.SendCommand.Execute(null);
    }

    private async void ModelPicker_DropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) await viewModel.RefreshModelsAsync();
    }

    private async void AttachProject_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        if (!StorageProvider.CanPickFolder)
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local folder picker.");
            return;
        }
        viewModel.ReportContextActionStatus("Opening local project folder picker…");
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Attach a local project folder",
                AllowMultiple = false
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (path is null)
            {
                viewModel.ReportContextActionStatus("No project folder was selected.");
                return;
            }
            if (viewModel.FindMostRecentConversationForProject(path) is { } recent)
            {
                var choice = await ShowProjectConversationChoice(path, recent);
                if (choice == "resume")
                {
                    viewModel.ResumeProjectConversation(recent);
                    return;
                }
                if (choice == "new") viewModel.NewConversation();
                else if (choice != "attach") return;
            }
            viewModel.SetProjectFolder(path);
            if (!viewModel.IsProjectPathKnown(path) && !viewModel.IsProjectPathTrusted(path) && viewModel.CanManageProjectTrust)
            {
                var trustChoice = await ShowProjectTrustDialog(path);
                if (trustChoice == "folder") await viewModel.TrustProjectFolderAsync(path);
                else if (trustChoice == "parent") await viewModel.TrustProjectFolderAsync(path, includeSubfolders: true);
                else await viewModel.MarkProjectFolderKnownAsync(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            viewModel.ReportContextActionStatus($"Could not attach project folder: {ex.Message}");
        }
    }

    private async Task<string?> ShowProjectConversationChoice(string projectPath, Codev.Conversation recent)
    {
        var dialog = new Window
        {
            Title = "Continue work in this project?",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var choices = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        foreach (var (label, value) in new[]
                 {
                     ("Cancel", "cancel"), ("Attach here", "attach"), ("New conversation", "new"), ("Resume recent", "resume")
                 })
        {
            var button = new Button { Content = label, Classes = { "soft" }, Tag = value };
            button.Click += (_, _) => dialog.Close(button.Tag?.ToString());
            choices.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Codev found a recent conversation for:\n{projectPath}", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = $"{recent.Title}\nUpdated {recent.UpdatedAt.ToLocalTime():g} · {recent.Messages.Count} message(s)", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush },
                new TextBlock { Text = "Resume it, attach this folder to the current conversation, or start a fresh conversation for this project.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                choices
            }
        };
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<string?> ShowProjectTrustDialog(string projectPath)
    {
        var parent = Directory.GetParent(Path.GetFullPath(projectPath))?.FullName ?? projectPath;
        var parentIsRoot = string.Equals(parent, Path.GetPathRoot(parent), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var status = new TextBlock
        {
            Text = "This folder is untrusted. Chat and read-only browsing remain available. Codev will only include files you explicitly select until you trust this folder. Trust is stored locally. Avalonia chat currently does not load project instructions, skills, commands, hooks, or MCP settings.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 510
        };
        var actions = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                new Button { Content = "Keep untrusted", Tag = "untrusted" },
                new Button { Content = parentIsRoot ? "Parent is filesystem root" : "Trust parent and subfolders", Tag = "parent", IsEnabled = !parentIsRoot },
                new Button { Content = "Trust folder", Tag = "folder" }
            }
        };
        var dialog = new Window
        {
            Title = "Project folder trust",
            Width = 570,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = projectPath, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                    status,
                    new TextBlock { Text = $"Parent folder: {parent}", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 11 },
                    actions
                }
            }
        };
        foreach (var button in actions.Children.OfType<Button>()) button.Click += (_, _) => dialog.Close(button.Tag?.ToString());
        return await dialog.ShowDialog<string?>(this);
    }

    private async void ToggleProjectTrust_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        try { await viewModel.ToggleProjectFolderTrustAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            viewModel.ReportContextActionStatus($"Could not update folder trust: {ex.Message}");
        }
    }

    private async void ReviewFileChanges_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || !viewModel.CanReviewFileChanges || viewModel.ActiveConversation is not { } conversation) return;
        var entries = conversation.FileChanges.OrderByDescending(change => change.ChangedAt).ToArray();
        var list = new ListBox();
        foreach (var change in entries)
        {
            var canRestore = !change.PreviousFileExisted || !string.IsNullOrWhiteSpace(change.CheckpointPath);
            var item = new ListBoxItem
            {
                Tag = change,
                Content = new TextBlock { Text = $"{change.RelativePath}\n{change.Kind} · {change.ChangedAt.LocalDateTime:g}", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                IsEnabled = canRestore,
                Padding = new Thickness(10, 7)
            };
            if (!canRestore) ToolTip.SetTip(item, "This history entry came from a backup, which does not include its local rollback checkpoint.");
            list.Items.Add(item);
        }
        var restore = new Button { Content = "Review & restore…", Classes = { "soft" }, IsEnabled = false };
        var close = new Button { Content = "Close", Classes = { "soft" } };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { close, restore } };
        var dialog = new Window
        {
            Title = "Changed files",
            Width = 560,
            Height = 480,
            MinWidth = 430,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                Margin = new Thickness(18),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children =
                {
                    new TextBlock { Text = $"{entries.Length} change record(s) across {entries.Select(change => change.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} file(s). Newest first. Restores show the complete replacement and save the current file as a new checkpoint.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush },
                    list,
                    buttons
                }
            }
        };
        var content = (Grid)dialog.Content!;
        Grid.SetRow(list, 1);
        Grid.SetRow(buttons, 2);
        list.SelectionChanged += (_, _) => restore.IsEnabled = list.SelectedItem is ListBoxItem { Tag: Codev.FileChangeRecord } && viewModel.CanReviewFileChanges;
        close.Click += (_, _) => dialog.Close();
        restore.Click += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: Codev.FileChangeRecord change }) return;
            dialog.Close();
            await ReviewAndRestoreChangeAsync(viewModel, conversation, change);
        };
        await dialog.ShowDialog(this);
    }

    private async Task ReviewAndRestoreChangeAsync(ViewModels.MainViewModel viewModel, Codev.Conversation conversation, Codev.FileChangeRecord change)
    {
        if (conversation.ProjectPath is not { } projectPath) return;
        try
        {
            var files = new Codev.WorkspaceFileService(projectPath);
            var currentExists = File.Exists(files.ResolvePath(change.RelativePath));
            var current = currentExists ? await files.ReadFileSnapshotAsync(change.RelativePath) : null;
            if (!change.PreviousFileExisted && !currentExists)
            {
                await ShowGitInfoAsync("Already restored", $"{change.RelativePath} is already absent. No file changes were made.");
                return;
            }
            var previous = change.PreviousFileExisted
                ? await files.ReadCheckpointAsync(change.RelativePath, conversation.Id, change.CheckpointPath ?? "")
                : "[This restore will delete the file]";
            if (!await ReviewFileRestoreAsync(change.RelativePath, current?.Content ?? "[The file does not currently exist]", previous,
                    change.PreviousFileExisted, currentExists)) return;

            var rollback = await files.RestoreFileStateAsync(change.RelativePath, conversation.Id, change.PreviousFileExisted,
                change.CheckpointPath, current?.Sha256);
            conversation.FileChanges.Remove(change);
            conversation.FileChanges.Add(new Codev.FileChangeRecord(change.RelativePath, rollback, DateTimeOffset.Now, "Restore", currentExists));
            await viewModel.SaveFileChangesAsync();
            await ShowGitInfoAsync("File restored", $"Restored {change.RelativePath}. A checkpoint of the replaced version is available in this history.");
        }
        catch (Exception ex)
        {
            await ShowGitInfoAsync("Could not restore checkpoint", $"The current file was left unchanged.\n\n{ex.Message}");
        }
    }

    private async Task<bool> ReviewFileRestoreAsync(string relativePath, string current, string restored, bool previousFileExisted, bool currentFileExists)
    {
        var layout = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        layout.Children.Add(new TextBlock
        {
            Text = previousFileExisted
                ? $"Review restoring {relativePath}. Approving replaces this one file with its checkpoint and saves the current version as a new checkpoint."
                : $"Review deleting {relativePath}. Approving removes this one file and saves it as a checkpoint first.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        TextBox ReviewBox(string content) => new()
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinWidth = 360,
            Background = this.FindResource("ComposerBrush") as global::Avalonia.Media.IBrush,
            Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
        };
        var currentPane = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "CURRENT", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush }, new ScrollViewer { Content = ReviewBox(current), Height = 440, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } } };
        var restoredPane = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = previousFileExisted ? "RESTORED CHECKPOINT" : "AFTER RESTORE", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush }, new ScrollViewer { Content = ReviewBox(restored), Height = 440, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } } };
        Grid.SetColumn(restoredPane, 1);
        panes.Children.Add(currentPane);
        panes.Children.Add(restoredPane);
        layout.Children.Add(panes);
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var keep = new Button { Content = "Keep current version", Classes = { "soft" } };
        var approve = new Button { Content = previousFileExisted ? "Approve & restore file" : "Approve & delete file", Classes = { "soft" }, IsEnabled = currentFileExists || previousFileExisted };
        var dialog = new Window { Title = "Review file restore", Width = 900, Height = 600, MinWidth = 740, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        keep.Click += (_, _) => dialog.Close(false);
        approve.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(keep);
        buttons.Children.Add(approve);
        layout.Children.Add(buttons);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void GitStatus_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.ActiveConversation?.ProjectPath is not { } projectPath || !Directory.Exists(projectPath)) return;
        var service = new Codev.GitRepositoryService(projectPath);
        Codev.GitRepositoryStatus status;
        IReadOnlyList<string> branches;
        try
        {
            status = await service.GetStatusAsync();
            branches = await service.GetLocalBranchesAsync();
        }
        catch (Exception ex)
        {
            await ShowGitInfoAsync("Git status is unavailable", ex.Message);
            return;
        }

        var dialog = new Window
        {
            Title = "Git status · " + Path.GetFileName(status.Root),
            Width = 980,
            Height = 680,
            MinWidth = 680,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = null
        };
        var layout = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new TextBlock { Text = status.Root, FontSize = 11, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush, TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis });
        var branchRow = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        branchRow.Children.Add(new TextBlock { Text = "Branch", VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center });
        var branchPicker = new ComboBox { MinWidth = 170, ItemsSource = branches, SelectedItem = status.Branch };
        branchRow.Children.Add(branchPicker);
        var switchBranch = new Button { Content = "Switch…", Classes = { "soft" } };
        var createBranch = new Button { Content = "New branch…", Classes = { "soft" } };
        var refresh = new Button { Content = "Refresh", Classes = { "soft" } };
        branchRow.Children.Add(switchBranch);
        branchRow.Children.Add(createBranch);
        branchRow.Children.Add(refresh);
        header.Children.Add(branchRow);
        var summary = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        header.Children.Add(summary);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*"), ColumnSpacing = 10, Margin = new Thickness(0, 14, 0, 12) };
        var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var files = new ListBox();
        var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 8, 0, 0) };
        var stage = new Button { Content = "Stage selected", Classes = { "soft" }, IsEnabled = false };
        var commit = new Button { Content = "Review staged diff & commit…", Classes = { "soft" }, IsEnabled = false };
        actions.Children.Add(stage);
        actions.Children.Add(commit);
        Grid.SetRow(actions, 1);
        left.Children.Add(files);
        left.Children.Add(actions);
        body.Children.Add(left);

        var diffBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(10),
            Background = this.FindResource("SurfaceBrush") as global::Avalonia.Media.IBrush,
            Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
        };
        var askAboutDiff = new Button { Content = "Ask Codev about selection", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), IsEnabled = false };
        ToolTip.SetTip(askAboutDiff, "Add selected diff lines to the composer without sending them");
        var commentOnDiff = new Button { Content = "Comment on selection", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), IsEnabled = false };
        ToolTip.SetTip(commentOnDiff, "Attach a review comment to selected diff lines; it will not be sent until you send a prompt");
        var diffActions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { askAboutDiff, commentOnDiff } };
        var diffPanel = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var diffScroll = new ScrollViewer { Content = diffBox, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        diffPanel.Children.Add(diffScroll);
        Grid.SetRow(diffActions, 1);
        diffPanel.Children.Add(diffActions);
        Grid.SetColumn(diffPanel, 1);
        body.Children.Add(diffPanel);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var footer = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var close = new Button { Content = "Close", Classes = { "soft" } };
        close.Click += (_, _) => dialog.Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        dialog.Content = layout;

        var diffRevision = 0;
        void RenderStatus(Codev.GitRepositoryStatus value)
        {
            var tracking = value.Upstream is null ? "no upstream" : value.Upstream + (value.Ahead > 0 || value.Behind > 0 ? $" · ahead {value.Ahead}, behind {value.Behind}" : " · up to date");
            summary.Text = value.HasChanges ? $"{value.Files.Count} changed file(s) · {tracking}" : $"Working tree clean · {tracking}";
            files.Items.Clear();
            stage.IsEnabled = false;
            diffBox.Text = value.HasChanges ? "Select a changed file to inspect its staged and unstaged diff." : "The working tree is clean.";
            if (!value.HasChanges) files.Items.Add(new ListBoxItem { Content = "No staged, unstaged, or untracked changes.", IsEnabled = false });
            foreach (var file in value.Files)
                files.Items.Add(new ListBoxItem
                {
                    Content = $"{file.State.Replace(' ', '·')}   {file.DisplayPath}",
                    Tag = file,
                    FontFamily = new global::Avalonia.Media.FontFamily("Consolas")
                });
            commit.IsEnabled = value.Files.Any(file => file.Staged != " ");
            UpdateBranchButtons(value);
        }

        void UpdateBranchButtons(Codev.GitRepositoryStatus value)
        {
            var selected = branchPicker.SelectedItem as string;
            switchBranch.IsEnabled = !value.HasChanges && branches.Count > 0 && selected is not null && selected != value.Branch;
            createBranch.IsEnabled = !value.HasChanges;
        }

        async Task RefreshAsync()
        {
            try
            {
                status = await service.GetStatusAsync();
                branches = await service.GetLocalBranchesAsync();
                branchPicker.ItemsSource = branches;
                branchPicker.SelectedItem = branches.Contains(status.Branch, StringComparer.Ordinal) ? status.Branch : null;
                RenderStatus(status);
            }
            catch (Exception ex)
            {
                summary.Text = "Could not refresh Git status: " + ex.Message;
                files.Items.Clear();
                switchBranch.IsEnabled = false;
                createBranch.IsEnabled = false;
                stage.IsEnabled = false;
                commit.IsEnabled = false;
                diffBox.Text = "Git status could not be loaded.";
            }
        }

        files.SelectionChanged += async (_, _) =>
        {
            var revision = ++diffRevision;
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus file })
            {
                stage.IsEnabled = false;
                askAboutDiff.IsEnabled = false;
                return;
            }
            askAboutDiff.IsEnabled = false;
            stage.Content = file.Staged != " " && file.WorkingTree == " " ? "Unstage selected" : "Stage selected";
            stage.IsEnabled = true;
            diffBox.Text = "Loading diff…";
            try
            {
                var diff = await service.GetFileDiffAsync(file);
                if (revision == diffRevision)
                    diffBox.Text = string.IsNullOrWhiteSpace(diff) ? "Git reported no textual diff for this file (it may be binary or unchanged since status was refreshed)." : diff;
            }
            catch (Exception ex) { if (revision == diffRevision) diffBox.Text = "Could not load diff: " + ex.Message; }
        };
        diffBox.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name is "SelectionStart" or "SelectionEnd")
            {
                var hasSelection = files.SelectedItem is ListBoxItem { Tag: Codev.GitFileStatus } && !string.IsNullOrWhiteSpace(diffBox.SelectedText);
                askAboutDiff.IsEnabled = hasSelection;
                commentOnDiff.IsEnabled = hasSelection;
            }
        };
        askAboutDiff.Click += (_, _) =>
        {
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus file } || string.IsNullOrWhiteSpace(diffBox.SelectedText)) return;
            viewModel.Draft = Codev.GitDiffPromptBuilder.AppendSelection(viewModel.Draft, file.DisplayPath, diffBox.SelectedText);
            dialog.Close();
            Dispatcher.UIThread.Post(() =>
            {
                ComposerTextBox.Focus();
                ComposerTextBox.CaretIndex = (ComposerTextBox.Text ?? "").Length;
            }, DispatcherPriority.Background);
        };
        commentOnDiff.Click += async (_, _) =>
        {
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus file } || string.IsNullOrWhiteSpace(diffBox.SelectedText)) return;
            var selectedDiff = diffBox.SelectedText;
            var commentBox = new TextBox { Watermark = "What should Codev review about these lines?", AcceptsReturn = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180 };
            var content = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
            content.Children.Add(new TextBlock { Text = $"Add an unsent comment for {file.DisplayPath}", FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
            content.Children.Add(new TextBlock { Text = selectedDiff.Length > 1200 ? selectedDiff[..1200] + "…" : selectedDiff, FontFamily = new global::Avalonia.Media.FontFamily("Consolas"), FontSize = 10, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MaxHeight = 130 });
            content.Children.Add(commentBox);
            var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
            var cancelComment = new Button { Content = "Cancel", Classes = { "soft" } };
            var addComment = new Button { Content = "Add comment", Classes = { "soft" } };
            actions.Children.Add(cancelComment);
            actions.Children.Add(addComment);
            content.Children.Add(actions);
            var commentDialog = new Window { Title = "Comment on diff", Width = 580, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content };
            cancelComment.Click += (_, _) => commentDialog.Close(false);
            addComment.Click += (_, _) => commentDialog.Close(true);
            if (await commentDialog.ShowDialog<bool>(dialog) != true) return;
            if (!viewModel.AddPendingDiffComment(file.DisplayPath, selectedDiff, commentBox.Text ?? ""))
            {
                await ShowGitInfoAsync("Could not add diff comment", "Enter a comment and keep the selected diff and comment within their supported length limits.", dialog);
                return;
            }
            dialog.Close();
            Dispatcher.UIThread.Post(() => ComposerTextBox.Focus(), DispatcherPriority.Background);
        };
        branchPicker.SelectionChanged += (_, _) => UpdateBranchButtons(status);
        refresh.Click += async (_, _) => await RefreshAsync();
        stage.Click += async (_, _) =>
        {
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus file }) return;
            try
            {
                if (file.Staged != " " && file.WorkingTree == " ") await service.UnstageFileAsync(file.Path);
                else await service.StageFileAsync(file.Path);
                await RefreshAsync();
            }
            catch (Exception ex) { await ShowGitInfoAsync("Could not update the Git index", ex.Message, dialog); await RefreshAsync(); }
        };
        switchBranch.Click += async (_, _) =>
        {
            if (branchPicker.SelectedItem is not string selected) return;
            if (!await ConfirmGitActionAsync(dialog, "Switch local branch?", $"Switch to {selected}? The working tree must be clean.")) return;
            try { await service.SwitchBranchAsync(selected); await RefreshAsync(); }
            catch (Exception ex) { await ShowGitInfoAsync("Could not switch branch", ex.Message, dialog); await RefreshAsync(); }
        };
        createBranch.Click += async (_, _) =>
        {
            var nameBox = new TextBox { Watermark = "feature/my-change", MinWidth = 300 };
            var createWindow = new Window
            {
                Title = "Create local branch",
                Width = 430,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = "Create and switch to a new branch. The working tree must be clean." }, nameBox } }
            };
            var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
            var cancel = new Button { Content = "Cancel", Classes = { "soft" } };
            var create = new Button { Content = "Create branch", Classes = { "soft" } };
            cancel.Click += (_, _) => createWindow.Close(false);
            create.Click += (_, _) => createWindow.Close(true);
            buttons.Children.Add(cancel);
            buttons.Children.Add(create);
            ((StackPanel)createWindow.Content!).Children.Add(buttons);
            if (await createWindow.ShowDialog<bool>(dialog) != true) return;
            try { await service.CreateAndSwitchBranchAsync(nameBox.Text ?? ""); await RefreshAsync(); }
            catch (Exception ex) { await ShowGitInfoAsync("Could not create branch", ex.Message, dialog); await RefreshAsync(); }
        };
        commit.Click += async (_, _) => await ReviewAndCommitAsync(service, dialog, RefreshAsync);
        RenderStatus(status);
        await dialog.ShowDialog(this);
    }

    private async Task ReviewAndCommitAsync(Codev.GitRepositoryService service, Window owner, Func<Task> refresh)
    {
        Codev.GitStagedReview stagedReview;
        try { stagedReview = await service.GetStagedReviewAsync(); }
        catch (Exception ex) { await ShowGitInfoAsync("Could not review staged changes", ex.Message, owner); return; }
        if (string.IsNullOrWhiteSpace(stagedReview.Diff))
        {
            await ShowGitInfoAsync("No textual changes to review", "The staged changes do not contain a textual diff. No commit was created.", owner);
            return;
        }
        var layout = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        layout.Children.Add(new TextBlock { Text = "Review the exact staged diff. Unstaged working-tree edits will not be included.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
        var diff = new TextBox { Text = stagedReview.Diff, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap, FontFamily = new global::Avalonia.Media.FontFamily("Consolas"), FontSize = 11, Padding = new Thickness(10), Background = this.FindResource("SurfaceBrush") as global::Avalonia.Media.IBrush, Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush };
        layout.Children.Add(new ScrollViewer { Content = diff, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Height = 440 });
        var message = new TextBox { Watermark = "Commit message", MinWidth = 360 };
        layout.Children.Add(message);
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" } };
        var commit = new Button { Content = "Create local commit", Classes = { "soft" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(commit);
        layout.Children.Add(buttons);
        var dialog = new Window { Title = "Review staged changes", Width = 900, Height = 620, MinWidth = 640, MinHeight = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        cancel.Click += (_, _) => dialog.Close();
        commit.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(message.Text)) { await ShowGitInfoAsync("Commit message required", "Enter a commit message first.", dialog); return; }
            if (!await ConfirmGitActionAsync(dialog, "Create local commit?", $"{message.Text.Trim()}\n\nThis creates a local commit and will not push to GitHub.")) return;
            commit.IsEnabled = false;
            try
            {
                await service.CommitAsync(message.Text, stagedReview);
                dialog.Close();
                await refresh();
            }
            catch (Exception ex) { commit.IsEnabled = true; await ShowGitInfoAsync("Could not create commit", ex.Message, dialog); }
        };
        await dialog.ShowDialog(owner);
    }

    private async Task<bool> ConfirmGitActionAsync(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap } } }
        };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" } };
        var confirm = new Button { Content = "Confirm", Classes = { "soft" } };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        ((StackPanel)dialog.Content!).Children.Add(buttons);
        return await dialog.ShowDialog<bool>(owner);
    }

    private async Task ShowGitInfoAsync(string title, string message, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap }, new Button { Content = "Close", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right } } }
        };
        var panel = (StackPanel)dialog.Content!;
        ((Button)panel.Children[1]).Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner ?? this);
    }

    private static async Task ShowReadOnlyReviewAsync(string report, Window owner)
    {
        var text = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 360,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Top
        };
        var close = new Button { Content = "Close", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, IsCancel = true };
        var layout = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children =
        {
            new TextBlock { Text = "Read-only model review · second opinion", FontSize = 15, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            new TextBlock { Text = "The model reviewed a bounded local Git diff. No project files or Git state were changed.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
            text,
            close
        }};
        var dialog = new Window { Title = "Uncommitted changes review", Width = 760, Height = 620, MinWidth = 560, MinHeight = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    private async void AddContextFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || !viewModel.HasProject || viewModel.ActiveConversation?.ProjectPath is not { } projectPath) return;
        if (!StorageProvider.CanOpen)
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local project file picker.");
            return;
        }
        viewModel.ReportContextActionStatus("Opening local project file picker…");
        try
        {
            var start = await StorageProvider.TryGetFolderFromPathAsync(projectPath);
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add project files as local chat context",
                AllowMultiple = true,
                SuggestedStartLocation = start,
                FileTypeFilter =
                [
                    new FilePickerFileType("Supported source and text files")
                    {
                        Patterns = ["*.cs", "*.xaml", "*.csproj", "*.sln", "*.md", "*.txt", "*.json", "*.js", "*.jsx", "*.ts", "*.tsx", "*.py", "*.html", "*.css", "*.sql", "*.xml", "*.yml", "*.yaml", "*.toml", "*.props", "*.targets", "*.ps1", "*.sh", "*.bat"]
                    },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
            var paths = files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray();
            if (paths.Length == 0)
            {
                viewModel.ReportContextActionStatus("No project files were selected.");
                return;
            }
            viewModel.AddContextFiles(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            viewModel.ReportContextActionStatus($"Could not add project files: {ex.Message}");
        }
    }

    private async void BrowseProjectFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel ||
            viewModel.ActiveConversation?.ProjectPath is not { } projectPath || !Directory.Exists(projectPath)) return;
        try
        {
            var browser = new ProjectFileBrowserWindow(projectPath, viewModel.ActiveConversation.ContextFiles);
            var selectedPath = await browser.ShowDialog<string?>(this);
            if (selectedPath is not null && viewModel.AddProjectFileMention(selectedPath))
                viewModel.ReportContextActionStatus($"Added {selectedPath} to this conversation's context");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            viewModel.ReportContextActionStatus($"Could not browse project files: {ex.Message}");
        }
    }

    private async void ExportConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        if (!StorageProvider.CanSave)
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local save dialog.");
            return;
        }
        try
        {
            var conversation = viewModel.ActiveConversation;
            if (conversation is null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export conversation",
                SuggestedFileName = $"{SafeExportName(conversation.Title)}.md",
                DefaultExtension = "md",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Markdown document") { Patterns = ["*.md"] }]
            });
            if (file is null) return;
            var markdown = await viewModel.ExportActiveConversationMarkdownAsync();
            await WriteTextFileAsync(file, markdown);
            viewModel.ReportContextActionStatus($"Exported conversation · {file.Name}");
        }
        catch (Exception ex)
        {
            viewModel.ReportContextActionStatus($"Could not export conversation: {ex.Message}");
        }
    }

    private async void ExportConversationHtml_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.ActiveConversation is not { } conversation) return;
        var diffResult = await CollectConversationExportDiffsAsync(conversation);
        var candidates = Codev.ConversationSecretRedactor.FindCandidates(conversation,
            diffResult.Diffs.Select(pair => (pair.Value, $"Reviewed diff · {pair.Key}")));
        var redactions = await ReviewExportSecretsAsync(candidates);
        if (redactions is null) return;
        if (!StorageProvider.CanSave)
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local save dialog.");
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export conversation as standalone HTML",
                SuggestedFileName = $"{SafeExportName(conversation.Title)}.html",
                DefaultExtension = "html",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("HTML document") { Patterns = ["*.html", "*.htm"] }]
            });
            if (file is null) return;
            await WriteTextFileAsync(file, Codev.ConversationHtmlExporter.Export(conversation, redactions, diffResult.Diffs));
            viewModel.ReportContextActionStatus(diffResult.SkippedFiles == 0
                ? $"Exported standalone HTML · {file.Name}"
                : $"Exported standalone HTML · {file.Name} · {diffResult.SkippedFiles} file diff(s) unavailable or over the export size limit");
        }
        catch (Exception ex)
        {
            viewModel.ReportContextActionStatus($"Could not export conversation: {ex.Message}");
        }
    }

    private static async Task<(IReadOnlyDictionary<string, string> Diffs, int SkippedFiles)> CollectConversationExportDiffsAsync(Codev.Conversation conversation)
    {
        var diffs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (conversation.FileChanges.Count == 0) return (diffs, 0);
        if (string.IsNullOrWhiteSpace(conversation.ProjectPath) || !Directory.Exists(conversation.ProjectPath))
            return (diffs, conversation.FileChanges.Select(change => change.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        const int maxFiles = 50;
        const int maxTotalDiffCharacters = 200_000;
        var files = conversation.FileChanges
            .GroupBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var skipped = Math.Max(0, files.Length - maxFiles);
        var totalCharacters = 0;
        var workspace = new Codev.WorkspaceFileService(conversation.ProjectPath);
        foreach (var group in files.Take(maxFiles))
        {
            var first = group.OrderBy(change => change.ChangedAt).First();
            try
            {
                var before = first.PreviousFileExisted
                    ? string.IsNullOrWhiteSpace(first.CheckpointPath)
                        ? throw new IOException("The earliest saved checkpoint is unavailable.")
                        : await workspace.ReadCheckpointAsync(first.RelativePath, conversation.Id, first.CheckpointPath)
                    : "";
                var fullPath = workspace.ResolvePath(first.RelativePath);
                var after = File.Exists(fullPath) ? await workspace.ReadFileAsync(first.RelativePath) : "";
                if (string.Equals(before, after, StringComparison.Ordinal)) continue;
                if (before.Length + after.Length > 100_000)
                {
                    skipped++;
                    continue;
                }
                var diff = Codev.UnifiedDiff.Format(first.RelativePath, before, after);
                if (totalCharacters + diff.Length > maxTotalDiffCharacters)
                {
                    skipped++;
                    continue;
                }
                diffs[first.RelativePath] = diff;
                totalCharacters += diff.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                skipped++;
            }
        }
        return (diffs, skipped);
    }

    private async Task<IReadOnlyList<string>?> ReviewExportSecretsAsync(IReadOnlyList<Codev.SecretRedactionCandidate> candidates)
    {
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = candidates.Count == 0
                ? "Codev did not find common credential patterns in the transcript or included file diffs. This scan is only a heuristic and can miss secrets; review the export before sharing it."
                : $"Codev found {candidates.Count} possible secret(s) in the transcript or included file diffs. Selected matches will be replaced with [REDACTED]. Detection is heuristic and can miss secrets, so review the export before sharing it.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 620
        });
        var checks = new List<CheckBox>();
        if (candidates.Count > 0)
        {
            var list = new StackPanel { Spacing = 5 };
            foreach (var candidate in candidates)
            {
                var check = new CheckBox { Content = candidate.Display, IsChecked = true, Tag = candidate.Value };
                checks.Add(check);
                list.Children.Add(check);
            }
            content.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 300,
                VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            });
        }
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        var dialog = new Window
        {
            Title = "Review possible secrets",
            Width = 680,
            MinWidth = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.FindResource("AppBackgroundBrush") as global::Avalonia.Media.IBrush,
            Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush,
            Content = content
        };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" } };
        var exportOriginal = new Button { Content = "Export without redaction", Classes = { "soft" } };
        var exportRedacted = new Button { Content = "Redact selected & continue" };
        cancel.Click += (_, _) => dialog.Close(null);
        exportOriginal.Click += (_, _) => dialog.Close(Array.Empty<string>());
        exportRedacted.Click += (_, _) => dialog.Close(checks
            .Where(check => check.IsChecked == true && check.Tag is string)
            .Select(check => (string)check.Tag!).ToArray());
        buttons.Children.Add(cancel);
        buttons.Children.Add(exportOriginal);
        buttons.Children.Add(exportRedacted);
        content.Children.Add(buttons);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(this);
    }

    private async void RenameConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || GetMenuConversation(menuItem) is not { } conversation || DataContext is not ViewModels.MainViewModel viewModel) return;
        var input = new TextBox { Text = conversation.Title, MinWidth = 360, Margin = new Thickness(0, 10, 0, 18) };
        var save = new Button { Content = "Save name", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 100 };
        var dialog = new Window
        {
            Title = "Rename conversation",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children = { new TextBlock { Text = "Conversation name" }, input, save }
            }
        };
        save.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(true);
        };
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        if (await dialog.ShowDialog<bool>(this) == true)
            await viewModel.RenameConversationAsync(conversation, input.Text ?? "");
    }

    private async void ForkConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetMenuConversation(menuItem) is { } conversation && DataContext is ViewModels.MainViewModel viewModel)
            await viewModel.ForkConversationAsync(conversation);
    }

    private void ArchiveConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetMenuConversation(menuItem) is { } conversation && DataContext is ViewModels.MainViewModel viewModel)
            viewModel.ToggleConversationArchive(conversation);
    }

    private async void DeleteConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || GetMenuConversation(menuItem) is not { } conversation || DataContext is not ViewModels.MainViewModel viewModel) return;
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var delete = new Button { Content = "Delete conversation", MinWidth = 150 };
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, delete }
        };
        var dialog = new Window
        {
            Title = "Delete conversation?",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Permanently delete ‘{conversation.Title}’ and its saved messages? This cannot be undone.",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
                    },
                    buttons
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        delete.Click += (_, _) => dialog.Close(true);
        if (await dialog.ShowDialog<bool>(this) == true)
            await viewModel.DeleteConversationAsync(conversation);
    }

    private static Codev.Conversation? GetMenuConversation(MenuItem item) =>
        item.Tag as Codev.Conversation ??
        (item.Parent as ContextMenu)?.PlacementTarget?.DataContext as Codev.Conversation;

    private async void ExportBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        if (!StorageProvider.CanSave)
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local save dialog.");
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export all Codev conversations",
                SuggestedFileName = $"codev-backup-{DateTime.Now:yyyy-MM-dd}.codev.json",
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Codev conversation backup") { Patterns = ["*.codev.json", "*.json"] }]
            });
            if (file is null) return;
            var backup = await viewModel.ExportConversationBackupAsync();
            await WriteTextFileAsync(file, backup);
            viewModel.ReportContextActionStatus($"Backup saved · {file.Name}");
        }
        catch (Exception ex)
        {
            viewModel.ReportContextActionStatus($"Could not create conversation backup: {ex.Message}");
        }
    }

    private async void ImportBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        if (!StorageProvider.CanOpen)
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local file picker.");
            return;
        }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Codev conversation backup",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Codev conversation backup") { Patterns = ["*.codev.json", "*.json"] }]
            });
            var file = files.FirstOrDefault();
            if (file is null) return;
            viewModel.ReportContextActionStatus($"Reading backup · {file.Name}");
            var json = await ReadTextFileAsync(file, 100_000_000);
            var importedCount = await viewModel.ImportConversationBackupAsync(json);
            viewModel.ReportContextActionStatus($"Imported {importedCount} conversation(s). Existing history was left unchanged.");
        }
        catch (Exception ex)
        {
            viewModel.ReportContextActionStatus($"Could not import backup. Existing history was left unchanged. {ex.Message}");
        }
    }

    private static async Task WriteTextFileAsync(IStorageFile file, string contents)
    {
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(contents);
    }

    private static async Task<string> ReadTextFileAsync(IStorageFile file, int maxBytes)
    {
        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > maxBytes)
            throw new InvalidDataException("The selected backup is larger than the 100 MB import limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory());
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("The selected backup is larger than the 100 MB import limit.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    private static string SafeExportName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(title.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "codev-conversation" : cleaned;
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var primaryModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (primaryModifier && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.M)
            viewModel.CycleConversationMode();
        else if (primaryModifier && e.Key == Key.N)
            viewModel.NewConversationCommand.Execute(null);
        else if (primaryModifier && e.Key == Key.F)
            SearchTextBox.Focus();
        else if (primaryModifier && e.Key == Key.L)
            ComposerTextBox.Focus();
        else if (e.Key == Key.Escape && viewModel.IsGenerating)
            viewModel.StopGenerationCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }
}

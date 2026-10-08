using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Codev.Avalonia;
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
    private TaskCompletionSource<bool>? _pendingFileApproval;
    private TaskCompletionSource<Codev.ProjectCommandApprovalChoice>? _pendingCommandApproval;
    private TaskCompletionSource<Codev.ProjectCommandApprovalChoice>? _pendingMcpApproval;
    private TaskCompletionSource<bool>? _pendingProfileToolApproval;
    private Window? _keyboardShortcutsWindow;
    private bool _shutdownStarted;
    private bool _closeAfterShutdown;
    private readonly IConversationBackupPicker? _conversationBackupPicker;

    public MainWindow() : this(null) { }

    public MainWindow(IConversationBackupPicker? conversationBackupPicker)
    {
        _conversationBackupPicker = conversationBackupPicker;
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, MainWindow_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        ComposerTextBox.AddHandler(InputElement.KeyDownEvent, Composer_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(ComposerTextBox, true);
        DragDrop.AddDragOverHandler(ComposerTextBox, Composer_DragOver);
        DragDrop.AddDropHandler(ComposerTextBox, Composer_Drop);
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _fileMentionSearch?.Cancel();
            _slashCommandSearch?.Cancel();
            ClearInlineApproval();
        };
        DataContextChanged += (_, _) => ObserveMessages();
        DataContextChanged += (_, _) => ConfigureAgentInteractions();
        ObserveMessages();
        ConfigureAgentInteractions();
        Opened += (_, _) =>
        {
            FitInsideCurrentScreenWorkArea();
            ScheduleScrollToLatest();
        };
    }

    private void FitInsideCurrentScreenWorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0) return;

        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var workArea = screen.WorkingArea;
        var maximumWidth = workArea.Width / scale - 48;
        var maximumHeight = workArea.Height / scale - 48;
        if (maximumWidth >= MinWidth) Width = Math.Min(Width, maximumWidth);
        if (maximumHeight >= MinHeight) Height = Math.Min(Height, maximumHeight);

        var outerWidth = (int)Math.Ceiling(Width * scale) + 16;
        var outerHeight = (int)Math.Ceiling(Height * scale) + 40;
        Position = new PixelPoint(
            workArea.X + Math.Max(0, (workArea.Width - outerWidth) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - outerHeight) / 2));
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAfterShutdown) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _ = FinishShutdownBeforeClosingAsync();
    }

    private async Task FinishShutdownBeforeClosingAsync()
    {
        try
        {
            if (DataContext is ViewModels.MainViewModel viewModel)
            {
                try { await viewModel.SavePendingDraftAsync(); }
                finally { await viewModel.StopBackgroundCommandsAndShutdownAsync(); }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Codev shutdown cleanup failed: {ex}");
        }
        finally
        {
            _closeAfterShutdown = true;
            Dispatcher.UIThread.Post(() => Close());
        }
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

    private void TaskChecklist_Click(object? sender, RoutedEventArgs e) => TaskChecklistPopup.IsOpen = true;

    private async void EditPromptMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || (sender as MenuItem)?.Tag is not Codev.ChatMessage message) return;
        if (message.IsQueued) await viewModel.EditQueuedMessageAsync(message.MessageIndex);
        else if (viewModel.EditPromptCommand.CanExecute(message.MessageIndex)) viewModel.EditPromptCommand.Execute(message.MessageIndex);
    }

    private async void OpenSideChat_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && (sender as MenuItem)?.Tag is Codev.ChatMessage message)
            await viewModel.OpenSideChatFromMessageAsync(message);
    }

    private void TurnOffQueuing_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) viewModel.SetQueueEnabled(false);
    }

    private void TurnOnQueuing_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) viewModel.SetQueueEnabled(true);
    }

    private void PrioritizeQueuedMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && (sender as Button)?.Tag is Codev.ChatMessage message)
            viewModel.PrioritizeQueuedMessage(message);
    }

    private void CancelQueuedMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && (sender as Button)?.Tag is Codev.ChatMessage message)
            viewModel.CancelQueuedMessage(message);
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
        viewModel.ChooseConversationRewindAsync = ChooseConversationRewindAsync;
        viewModel.ReviewAndRestoreCodeBeforeRewindAsync = ReviewAndRestoreCodeBeforeRewindAsync;
        viewModel.EditConversationPromptAsync = EditConversationPromptAsync;
        viewModel.ShowCompactionProposalAsync = ShowCompactionProposalAsync;
        viewModel.ApproveProjectCommandAsync = ApproveAgentCommandAsync;
        viewModel.ApproveMcpToolAsync = ApproveAgentMcpToolAsync;
        viewModel.ConfirmAgentProfileToolAsync = ConfirmAgentProfileToolAsync;
        viewModel.ConfirmRepeatedToolCallAsync = ConfirmRepeatedToolCallAsync;
        viewModel.ConfirmHostedCodeTaskConsentAsync = ConfirmHostedCodeTaskConsentAsync;
    }

    private void TogglePinnedConversations_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) viewModel.TogglePinnedConversationsExpanded();
    }

    private void ToggleRecentConversations_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) viewModel.ToggleRecentConversationsExpanded();
    }

    private async void ProjectCommandMode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || sender is not MenuItem { Tag: string modeName } ||
            !Enum.TryParse<Codev.ProjectCommandPermissionMode>(modeName, ignoreCase: true, out var mode) || !Enum.IsDefined(mode)) return;
        await viewModel.SetProjectCommandPermissionModeAsync(mode);
    }

    private void BestOfNAttempts_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel && sender is MenuItem { Tag: string value } &&
            int.TryParse(value, out var attempts))
            viewModel.SetBestOfNAttemptsForNextTurn(attempts);
    }

    private async Task<bool> ReviewAgentFileChangeAsync(string relativePath, string before, string after, bool isNewFile, string? proposedPatch, IReadOnlyList<string>? contextSources, string approvalReason)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearInlineApproval();
            _pendingFileApproval = tcs;
            InlineApprovalContent.Content = BuildFileApprovalContent(relativePath, before, after, isNewFile, proposedPatch, contextSources, approvalReason);
            InlineApprovalPanel.IsVisible = true;
        });
        return await tcs.Task;
    }

    private Control BuildFileApprovalContent(string relativePath, string before, string after, bool isNewFile, string? proposedPatch, IReadOnlyList<string>? contextSources, string approvalReason)
    {
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(new TextBlock
        {
            Text = isNewFile ? $"Review proposed new file: {relativePath}" : proposedPatch is not null ? $"Review proposed patch: {relativePath}" : $"Review proposed file change: {relativePath}",
            FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
            FontSize = 14
        });
        var warnings = Codev.InstructionFollowingContentDetector.Detect(after);
        if (warnings.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = "Advisory: proposed content resembles " + string.Join(", ", warnings) + ". This check does not determine whether approval is required.",
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
            });
        panel.Children.Add(new TextBlock { Text = approvalReason, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
        if (contextSources is { Count: > 0 })
            panel.Children.Add(new TextBlock { Text = "Untrusted context: " + string.Join(" · ", contextSources), FontSize = 10, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });

        var isDark = Application.Current?.ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
        var reviewBackground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#242424" : "#FFFFFF"));
        var reviewForeground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#ECECEC" : "#262522"));
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8, Height = 220 };
        TextBox ReadOnlyBox(string content) => new()
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
            FontSize = 11,
            Background = reviewBackground,
            Foreground = reviewForeground,
            CaretBrush = reviewForeground,
            BorderBrush = this.FindResource("FieldBorderBrush") as global::Avalonia.Media.IBrush,
            IsTabStop = false
        };
        Control Pane(string title, string content)
        {
            var box = new StackPanel { Spacing = 3 };
            box.Children.Add(new TextBlock { Text = title, FontSize = 10, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
            box.Children.Add(new ScrollViewer { Content = ReadOnlyBox(content), HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            return box;
        }
        panes.Children.Add(Pane(isNewFile ? "CURRENT · new file" : "CURRENT", before));
        var proposed = Pane(proposedPatch is null ? "PROPOSED" : "RESULTING FILE", after);
        Grid.SetColumn(proposed, 1);
        panes.Children.Add(proposed);
        var details = new StackPanel { Spacing = 7 };
        details.Children.Add(panes);
        if (proposedPatch is not null)
        {
            var patch = ReadOnlyBox(proposedPatch);
            details.Children.Add(new Expander
            {
                Header = "Proposed patch",
                IsExpanded = false,
                Content = new ScrollViewer { Content = patch, MaxHeight = 140, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
            });
        }
        panel.Children.Add(new Expander { Header = "Show current and proposed files", IsExpanded = false, Content = details });
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var keep = new Button { Content = "Keep unchanged", Classes = { "soft" } };
        var apply = new Button { Content = isNewFile ? "Approve & create" : "Approve & apply", Classes = { "soft" } };
        keep.Click += (_, _) => CompleteFileApproval(false);
        apply.Click += (_, _) => CompleteFileApproval(true);
        buttons.Children.Add(keep);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        return panel;
    }

    private async Task<Codev.ProjectCommandApprovalChoice> ApproveAgentCommandAsync(Codev.CodeTaskCommandProposal proposal)
    {
        var tcs = new TaskCompletionSource<Codev.ProjectCommandApprovalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearInlineApproval();
            _pendingCommandApproval = tcs;
            InlineApprovalContent.Content = BuildCommandApprovalContent(proposal);
            InlineApprovalPanel.IsVisible = true;
        });
        return await tcs.Task;
    }

    private async Task<Codev.ProjectCommandApprovalChoice> ApproveAgentMcpToolAsync(Codev.McpCodeTaskTool tool, System.Text.Json.JsonElement arguments)
    {
        var tcs = new TaskCompletionSource<Codev.ProjectCommandApprovalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearInlineApproval();
            _pendingMcpApproval = tcs;
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = $"Review MCP {tool.Operation.DisplayName()} call: {tool.ServerName} · {tool.ToolName}", FontWeight = global::Avalonia.Media.FontWeight.SemiBold, FontSize = 14 });
            panel.Children.Add(new TextBlock { Text = tool.Description, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
            var viewModel = DataContext as ViewModels.MainViewModel;
            if (viewModel is { CanPersistMcpToolPermissions: false } && !string.IsNullOrWhiteSpace(viewModel.McpToolPermissionLoadError))
                panel.Children.Add(new TextBlock
                {
                    Text = "Saved MCP permissions are unavailable. This call needs approval, and Allow + run cannot save a persistent rule. " + viewModel.McpToolPermissionLoadError,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    FontWeight = global::Avalonia.Media.FontWeight.SemiBold
                });
            var args = new TextBox
            {
                Text = arguments.GetRawText(), IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MinHeight = 70, MaxHeight = 180,
                FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
                Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#F2F2F2")),
                Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#242424"))
            };
            panel.Children.Add(args);
            panel.Children.Add(new TextBlock { Text = "The MCP server and its results are external and untrusted. Review the arguments before allowing this operation.", FontSize = 10, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
            var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
            void Add(string label, Codev.ProjectCommandApprovalChoice choice)
            {
                var button = new Button
                {
                    Content = label,
                    Classes = { "soft" },
                    IsEnabled = label != "Allow + run" || viewModel?.CanPersistMcpToolPermissions != false
                };
                if (label == "Allow + run" && viewModel?.CanPersistMcpToolPermissions == false)
                    ToolTip.SetTip(button, "Saved MCP permissions are unavailable; this rule cannot be stored. Run once remains available.");
                button.Click += (_, _) => CompleteMcpApproval(choice);
                buttons.Children.Add(button);
            }
            Add("Cancel", Codev.ProjectCommandApprovalChoice.Cancel);
            Add("Deny this operation", Codev.ProjectCommandApprovalChoice.DenyExactCommand);
            Add("Allow + run", Codev.ProjectCommandApprovalChoice.AllowExactCommand);
            Add("Run once", Codev.ProjectCommandApprovalChoice.RunOnce);
            panel.Children.Add(buttons);
            InlineApprovalContent.Content = panel;
            InlineApprovalPanel.IsVisible = true;
        });
        return await tcs.Task;
    }

    private async Task<bool> ConfirmAgentProfileToolAsync(Codev.AgentProfile profile, string toolName, System.Text.Json.JsonElement arguments)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearInlineApproval();
            _pendingProfileToolApproval = tcs;
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = $"Agent profile · {profile.Name} asks before {toolName.Replace('_', ' ')}", FontWeight = global::Avalonia.Media.FontWeight.SemiBold, FontSize = 14 });
            panel.Children.Add(new TextBlock { Text = profile.Description, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
            panel.Children.Add(new TextBlock { Text = "Review the tool arguments. Approval applies to this call only; project permissions still apply.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
            panel.Children.Add(new ScrollViewer
            {
                Content = new TextBox
                {
                    Text = arguments.GetRawText(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    MinHeight = 80, MaxHeight = 200, FontFamily = new global::Avalonia.Media.FontFamily("Cascadia Code"),
                    Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#F2F2F2")),
                    Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#242424"))
                }, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            });
            var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
            var cancel = new Button { Content = "Cancel", Classes = { "soft" } };
            var allow = new Button { Content = "Allow once", Classes = { "soft" } };
            cancel.Click += (_, _) => CompleteProfileToolApproval(false);
            allow.Click += (_, _) => CompleteProfileToolApproval(true);
            buttons.Children.Add(cancel);
            buttons.Children.Add(allow);
            panel.Children.Add(buttons);
            InlineApprovalContent.Content = panel;
            InlineApprovalPanel.IsVisible = true;
        });
        return await tcs.Task;
    }

    private Control BuildCommandApprovalContent(Codev.CodeTaskCommandProposal proposal)
    {
        var isDark = Application.Current?.ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
        var reviewBackground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#242424" : "#FFFFFF"));
        var reviewForeground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#F2F2F2" : "#202020"));
        var secondaryForeground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#C8C8C8" : "#55534F"));
        var panel = new StackPanel { Spacing = 7 };
        var permissionMode = (DataContext as ViewModels.MainViewModel)?.GetProjectCommandPermissionMode(proposal.ProjectPath)
            ?? Codev.ProjectCommandPermissionMode.AskEveryTime;
        panel.Children.Add(new TextBlock { Text = proposal.IsBackground ? "Review background command" : proposal.IsVerification ? "Review verification command" : "Review project command", FontWeight = global::Avalonia.Media.FontWeight.SemiBold, FontSize = 14 });
        panel.Children.Add(new TextBlock
        {
            Text = $"Runs through {proposal.ShellName} with your account permissions; Codev cannot sandbox it to the project folder. Verify the exact command against your request.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = secondaryForeground
        });
        if (!string.IsNullOrWhiteSpace(proposal.MatchingUntrustedSource))
        {
            var warningForeground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#FFD166" : "#8A4B00"));
            var warningBackground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#352A12" : "#FFF4D6"));
            var warning = new StackPanel { Spacing = 3 };
            warning.Children.Add(new TextBlock
            {
                Text = "POTENTIAL INSTRUCTION FOLLOWING",
                FontWeight = global::Avalonia.Media.FontWeight.Bold,
                Foreground = warningForeground
            });
            warning.Children.Add(new TextBlock
            {
                Text = $"This command matches instructions in untrusted output from {proposal.MatchingUntrustedSource}. Check it against your request before running it.",
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                Foreground = warningForeground
            });
            panel.Children.Add(new Border
            {
                Background = warningBackground,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7),
                Child = warning
            });
        }
        if (proposal.ContextSources is { Count: > 0 } contextSources)
        {
            var sourcesBox = new TextBox
            {
                Text = string.Join(Environment.NewLine, contextSources),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                MinHeight = 54,
                MaxHeight = 120,
                FontSize = 10,
                Background = reviewBackground,
                Foreground = reviewForeground,
                CaretBrush = reviewForeground,
                BorderBrush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#555555" : "#B8B5AF"))
            };
            global::Avalonia.Automation.AutomationProperties.SetName(sourcesBox, "Untrusted context sources");
            panel.Children.Add(new Expander
            {
                Header = $"Show untrusted context sources ({contextSources.Count})",
                IsExpanded = false,
                Content = new ScrollViewer
                {
                    Content = sourcesBox,
                    MaxHeight = 130,
                    VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                }
            });
        }
        panel.Children.Add(new TextBlock { Text = "Working directory: " + proposal.ProjectPath, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 10, Foreground = secondaryForeground });
        var command = new TextBox
        {
            Text = proposal.Command,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 76,
            MaxHeight = 150,
            Padding = new Thickness(10, 8),
            FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
            Background = reviewBackground,
            Foreground = reviewForeground,
            CaretBrush = reviewForeground,
            BorderBrush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(isDark ? "#555555" : "#B8B5AF"))
        };
        panel.Children.Add(command);
        if (proposal.IsBackground)
            panel.Children.Add(new TextBlock { Text = "This process can keep running after the response and may open a network port. Stop it from the Background commands list or close Codev. Output is captured with a size limit.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontWeight = global::Avalonia.Media.FontWeight.SemiBold, Foreground = secondaryForeground });
        panel.Children.Add(new TextBlock
        {
            Text = permissionMode == Codev.ProjectCommandPermissionMode.Auto
                ? "Auto normally runs commands without approval unless an exact saved deny rule blocks them."
                : !Codev.ProjectCommandPermissionRegistry.CanCreateAllowRule(proposal.Command)
                    ? $"This workspace is in {permissionMode switch { Codev.ProjectCommandPermissionMode.Allowlist => "Allowlist", Codev.ProjectCommandPermissionMode.ReadOnly => "Read-only", _ => "Ask-every-time" }} mode. An exact allow rule cannot be saved for Git or Codev app-data commands."
                    : permissionMode == Codev.ProjectCommandPermissionMode.Allowlist
                        ? "This workspace is in Allowlist mode. This command is not on the allowlist; allowing it adds this exact command to the project."
                        : permissionMode == Codev.ProjectCommandPermissionMode.ReadOnly
                            ? "This workspace is in Read-only mode. This command is not a recognized read-only inspection, so it needs approval."
                            : "This workspace is in Ask-every-time mode. Allowing it saves an exact project rule without changing the selected mode.",
            FontSize = 10,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = secondaryForeground
        });
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        void Add(string label, Codev.ProjectCommandApprovalChoice choice, bool enabled = true)
        {
            var button = new Button { Content = label, Classes = { "soft" }, IsEnabled = enabled };
            button.Click += (_, _) => CompleteCommandApproval(choice);
            buttons.Children.Add(button);
        }
        Add("Cancel", Codev.ProjectCommandApprovalChoice.Cancel);
        Add("Deny exact command", Codev.ProjectCommandApprovalChoice.DenyExactCommand);
        var canRemember = Codev.ProjectCommandPermissionRegistry.CanCreateAllowRule(proposal.Command);
        Add(canRemember ? "Allow exact command + run" : "Protected · no saved allow", Codev.ProjectCommandApprovalChoice.AllowExactCommand, canRemember);
        Add(proposal.IsVerification ? "Run once & verify" : "Run once", Codev.ProjectCommandApprovalChoice.RunOnce);
        panel.Children.Add(buttons);
        return panel;
    }

    private void CompleteFileApproval(bool approve)
    {
        var pending = _pendingFileApproval;
        _pendingFileApproval = null;
        InlineApprovalPanel.IsVisible = false;
        InlineApprovalContent.Content = null;
        pending?.TrySetResult(approve);
    }

    private void CompleteCommandApproval(Codev.ProjectCommandApprovalChoice choice)
    {
        var pending = _pendingCommandApproval;
        _pendingCommandApproval = null;
        InlineApprovalPanel.IsVisible = false;
        InlineApprovalContent.Content = null;
        pending?.TrySetResult(choice);
    }

    private void CompleteMcpApproval(Codev.ProjectCommandApprovalChoice choice)
    {
        var pending = _pendingMcpApproval;
        _pendingMcpApproval = null;
        InlineApprovalPanel.IsVisible = false;
        InlineApprovalContent.Content = null;
        pending?.TrySetResult(choice);
    }

    private void CompleteProfileToolApproval(bool approve)
    {
        var pending = _pendingProfileToolApproval;
        _pendingProfileToolApproval = null;
        InlineApprovalPanel.IsVisible = false;
        InlineApprovalContent.Content = null;
        pending?.TrySetResult(approve);
    }

    private void ClearInlineApproval()
    {
        _pendingFileApproval?.TrySetResult(false);
        _pendingFileApproval = null;
        _pendingCommandApproval?.TrySetResult(Codev.ProjectCommandApprovalChoice.Cancel);
        _pendingCommandApproval = null;
        _pendingMcpApproval?.TrySetResult(Codev.ProjectCommandApprovalChoice.Cancel);
        _pendingMcpApproval = null;
        _pendingProfileToolApproval?.TrySetResult(false);
        _pendingProfileToolApproval = null;
        InlineApprovalContent.Content = null;
        InlineApprovalPanel.IsVisible = false;
    }

    private Task<bool> ConfirmHostedCodeTaskConsentAsync() => ConfirmGitActionAsync(this, "Allow OpenAI Code task?",
        "For this conversation, Codev may send your prompts and tool or command results to the OpenAI API. OpenAI API usage may incur separate charges. If no folder is attached, Codev will create a private workspace. Sharing an attached project's files and instructions is a separate choice. File changes still require your review, and commands still follow project approval.");

    private async Task<Codev.ConversationRewindChoice> ChooseConversationRewindAsync(int messageIndex)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.ActiveConversation is not { } conversation ||
            messageIndex < 0 || messageIndex >= conversation.Messages.Count) return Codev.ConversationRewindChoice.Cancel;
        var prompt = conversation.Messages[messageIndex].Content;
        var excerpt = prompt.Length > 220 ? prompt[..220] + "…" : prompt;
        var hardLinkNotice = Codev.FileHardLinkInspector.IsSupportedPlatform
            ? " Hard-linked files are detected and refused."
            : " Hard-link checks are unavailable on this platform, so rewind cannot replace existing files.";
        var layout = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        layout.Children.Add(new TextBlock
        {
            Text = $"Choose what to rewind before this prompt. {excerpt}\n\nRewinding the conversation removes this prompt and all later messages, then puts this prompt back in the composer. Code rewind only covers Codev-managed file changes; shell and external edits are not included.{hardLinkNotice}",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        var dialog = new Window
        {
            Title = "Rewind",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        Button Choice(string label, Codev.ConversationRewindChoice choice)
        {
            var button = new Button { Content = label, Classes = { "soft" } };
            button.Click += (_, _) => dialog.Close(choice);
            return button;
        }
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close(Codev.ConversationRewindChoice.Cancel);
        buttons.Children.Add(cancel);
        buttons.Children.Add(Choice("Conversation only", Codev.ConversationRewindChoice.ConversationOnly));
        buttons.Children.Add(Choice("Code only", Codev.ConversationRewindChoice.CodeOnly));
        buttons.Children.Add(Choice("Code + conversation", Codev.ConversationRewindChoice.CodeAndConversation));
        layout.Children.Add(buttons);
        return await dialog.ShowDialog<Codev.ConversationRewindChoice>(this);
    }

    private async Task<Codev.CodeRewindReviewResult> ReviewAndRestoreCodeBeforeRewindAsync(Codev.Conversation conversation, int messageIndex)
    {
        if (conversation.ProjectPath is not { } projectPath)
        {
            await ShowGitInfoAsync("Code rewind unavailable", "This conversation has no project workspace to restore.");
            return Codev.CodeRewindReviewResult.Cancelled;
        }

        try
        {
            var files = new Codev.WorkspaceFileService(projectPath);
            var plan = await Codev.ConversationCodeRewindService.BuildPlanAsync(conversation, messageIndex, files);
            if (plan.Files.Count == 0)
            {
                await ShowGitInfoAsync("No recorded code changes", "There are no Codev-managed file changes after this prompt to restore. Files changed by commands or outside Codev are not covered.");
                return Codev.CodeRewindReviewResult.NoChanges;
            }

            foreach (var item in plan.Files)
            {
                if (!await ReviewFileRestoreAsync(item.RelativePath,
                        item.ExpectedCurrentContent ?? "[The file does not currently exist]",
                        item.RestoreContent ?? "[The file will be absent after rewind]",
                        item.RestoreFileExisted, item.ExpectedCurrentFileExisted))
                    return Codev.CodeRewindReviewResult.Cancelled;
            }

            await Codev.ConversationCodeRewindService.ApplyPlanAsync(conversation, plan, files);
            await ((ViewModels.MainViewModel)DataContext!).SaveFileChangesAsync();
            await ShowGitInfoAsync("Code restored", $"Restored {plan.Files.Count} file(s) to the state before the selected prompt. Rollback checkpoints were saved in Files history.");
            return Codev.CodeRewindReviewResult.Restored;
        }
        catch (Exception ex)
        {
            await ShowGitInfoAsync("Could not rewind code", ex.Message);
            return Codev.CodeRewindReviewResult.Cancelled;
        }
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
        viewModel.RefreshSemanticIndexStatus();
        var endpoint = new TextBox { Text = viewModel.OllamaEndpointDisplay, MinWidth = 380 };
        var embeddingModel = new TextBox { Text = viewModel.EmbeddingModel, MinWidth = 220 };
        var semanticStatus = new TextBlock { Text = viewModel.SemanticIndexStatus, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        var buildSemanticIndex = new Button { Content = "Build / update current project index", IsEnabled = viewModel.CanBuildSemanticIndex };
        var deleteSemanticIndex = new Button { Content = "Delete current project index", IsEnabled = viewModel.HasSemanticIndexForProject };
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
                    new Separator(),
                    new TextBlock { Text = "Local semantic search · Ollama embeddings model" },
                    embeddingModel,
                    new TextBlock { Text = "Indexes the attached project only when you click Build. The directory must be trusted. Source text and vectors stay on this machine. Uses an installed model such as nomic-embed-text; downloads are never automatic. Rebuild after project changes; updates process only changed chunks. Turn on Use semantic search in a conversation's Code task menu to use the index. Retrieved excerpts enter hosted requests only when workspace sharing is enabled.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MaxWidth = 550 },
                    new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { buildSemanticIndex, deleteSemanticIndex } },
                    semanticStatus,
                    status,
                    buttons
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        embeddingModel.TextChanged += (_, _) => viewModel.EmbeddingModel = embeddingModel.Text ?? "";
        buildSemanticIndex.Click += async (_, _) =>
        {
            buildSemanticIndex.IsEnabled = deleteSemanticIndex.IsEnabled = false;
            await viewModel.UpdateSemanticIndexAsync();
            semanticStatus.Text = viewModel.SemanticIndexStatus;
            deleteSemanticIndex.IsEnabled = viewModel.HasSemanticIndexForProject;
            buildSemanticIndex.IsEnabled = viewModel.CanBuildSemanticIndex;
        };
        deleteSemanticIndex.Click += (_, _) =>
        {
            viewModel.DeleteSemanticIndexForProject();
            semanticStatus.Text = viewModel.SemanticIndexStatus;
            deleteSemanticIndex.IsEnabled = viewModel.HasSemanticIndexForProject;
        };
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
        await viewModel.WaitForSavedCloudApiKeysAsync();
        var provider = new ComboBox { ItemsSource = new[] { "OpenAI", "Anthropic" }, SelectedIndex = 0, MinWidth = 180 };
        var apiKey = new TextBox { MinWidth = 360, PasswordChar = '•', Watermark = "Paste a key to replace the saved one (or leave blank)" };
        var apiKeyLabel = new TextBlock { Text = "API key · leave blank to reuse the saved key" };
        var replaceSavedKey = new Button { Content = "Replace saved key…", MinWidth = 140, Classes = { "soft" }, IsVisible = false };
        var replacementRequested = false;
        var acknowledgement = new CheckBox
        {
            Content = "I understand that prompts and conversation history go to this provider under its data policies, and API use may incur separate charges. Project files stay local unless I separately opt in below.",
            IsChecked = viewModel.CloudRequestsEnabled,
            MaxWidth = 390
        };
        var autoConnect = new CheckBox
        {
            IsChecked = viewModel.AutoConnectProvider is not null,
            MaxWidth = 390
        };
        var includeProjectContext = new CheckBox
        {
            Content = "Allow hosted coding tools to receive project files and tool output when requested",
            IsChecked = viewModel.IncludeProjectContextForHosted,
            MaxWidth = 390
        };
        var status = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        void ShowSavedKeyStatus()
        {
            var selectedProvider = provider.SelectedItem?.ToString() == "Anthropic" ? Codev.CloudModelProviders.Anthropic : Codev.CloudModelProviders.OpenAI;
            var hasSavedKey = viewModel.HasSavedCloudApiKey(selectedProvider);
            status.Text = hasSavedKey
                ? $"Saved {selectedProvider} API key found. Leave the key field blank; Codev will reuse it. You only need to enable hosted requests for this session."
                : "No saved key is available. Enter a key to save it after successful model discovery.";
            apiKeyLabel.IsVisible = !hasSavedKey || replacementRequested;
            apiKey.IsVisible = !hasSavedKey || replacementRequested;
            replaceSavedKey.IsVisible = hasSavedKey && !replacementRequested;
            acknowledgement.Content = hasSavedKey
                ? "Enable hosted requests using my saved key (prompts/history go to the provider; API charges may apply)"
                : "I understand that prompts and conversation history go to this provider under its data policies, and API use may incur separate charges. Project files stay local unless I separately opt in below.";
            autoConnect.Content = hasSavedKey
                ? $"Reconnect {selectedProvider} automatically on startup using the saved key"
                : "Reconnect this provider automatically on startup after saving a key";
            autoConnect.IsChecked = viewModel.AutoConnectProvider?.Equals(selectedProvider, StringComparison.OrdinalIgnoreCase) == true;
        }
        provider.SelectionChanged += (_, _) => { replacementRequested = false; ShowSavedKeyStatus(); };
        ShowSavedKeyStatus();
        replaceSavedKey.Click += (_, _) =>
        {
            replacementRequested = true;
            ShowSavedKeyStatus();
            apiKey.Focus();
        };
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
                    apiKeyLabel,
                    apiKey,
                    replaceSavedKey,
                    new TextBlock
                    {
                        Text = "Leave the field blank to reuse a saved key. A newly entered key is saved immediately using Windows Credential Manager, macOS Keychain, or Linux Secret Service, even if model discovery later fails. Keys are never written to Codev settings, chats, or backups.",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        MaxWidth = 600
                    },
                    acknowledgement,
                    autoConnect,
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
            ShowSavedKeyStatus();
            status.Text = viewModel.ConnectionStatus;
            if (connected)
            {
                viewModel.SetAutoConnectProvider(autoConnect.IsChecked == true ? providerId : null);
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
            replacementRequested = false;
            ShowSavedKeyStatus();
            status.Text = viewModel.ConnectionStatus;
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void OpenAgentProfiles_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        await OpenCommandFolderAsync(viewModel.UserAgentProfilesFolder, viewModel);
    }

    private async void ManageAgentProfiles_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        IReadOnlyList<Codev.AgentProfileDocument> documents;
        try { documents = await viewModel.GetUserAgentProfileDocumentsAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            viewModel.ReportContextActionStatus($"User agent profiles could not be opened ({ex.GetType().Name}): {ex.Message}");
            return;
        }
        var editor = new AgentProfileEditorWindow(viewModel, documents,
            () => OpenCommandFolderAsync(viewModel.UserAgentProfilesFolder, viewModel));
        await editor.ShowDialog(this);
    }

    private async void McpServers_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        string editorText;
        string? loadStatus = null;
        try
        {
            (editorText, loadStatus) = await viewModel.GetMcpServerConfigurationEditorStateAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMcpConfigurationUnavailableAsync(ex, viewModel);
            return;
        }
        var editor = new TextBox
        {
            Text = editorText,
            AcceptsReturn = true, TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new global::Avalonia.Media.FontFamily("Cascadia Code"), FontSize = 12,
            MinHeight = 320, MaxHeight = 540, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#F2F2F2")),
            Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#171717"))
        };
        var status = new TextBlock { Text = loadStatus ?? string.Empty, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(status, "McpConfigurationStatus");
        var close = new Button { Content = "Close", Classes = { "soft" } };
        var save = new Button { Content = "Save servers", Classes = { "soft" } };
        var forgetSignIns = new Button { Content = "Forget saved OAuth sign-ins", Classes = { "soft" } };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { forgetSignIns, close, save } };
        var dialog = new Window
        {
            Title = "MCP servers", Width = 760, Height = 680, MinWidth = 560, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "External tools for Code tasks", FontSize = 15, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = "Edit the server list as JSON. Use transport Stdio with command/arguments, or Http with a URL. Credentials are referenced by environment variable name and are never stored here. HTTP servers use OAuth by default; set OAuthEnabled to false for header-only authentication. OAuthClientId, OAuthClientSecretEnvironmentVariable, and OAuthScopes are optional. Sign-in opens your system browser when the server requests authorization, and tokens stay in the OS credential vault. Optional StartupTimeoutMs, CatalogTimeoutMs, and ExecutionTimeoutMs are bounded per-server limits. Servers connect on the next Code task.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                    new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
                    status, buttons
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        forgetSignIns.Click += async (_, _) =>
        {
            try
            {
                var count = await viewModel.ForgetMcpOAuthSignInsAsync();
                status.Text = count == 0
                    ? "No saved OAuth sign-ins were found for configured HTTP servers."
                    : $"Forgot {count} saved sign-in(s). The servers may ask you to sign in again.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                status.Text = $"Could not forget saved sign-ins ({ex.GetType().Name}).";
            }
        };
        save.Click += async (_, _) =>
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Codev.McpServerConfiguration>>(editor.Text ?? "[]",
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                    }) ?? [];
                await viewModel.SaveMcpServerConfigurationsAsync(parsed);
                dialog.Close();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status.Text = $"Could not save MCP servers ({ex.GetType().Name}): {ex.Message}";
            }
        };
        await dialog.ShowDialog(this);
    }

    private async Task ShowMcpConfigurationUnavailableAsync(Exception ex, ViewModels.MainViewModel viewModel)
    {
        var explanation = new TextBlock
        {
            Text = $"Codev could not read the saved MCP settings ({ex.GetType().Name}) and left the existing item unchanged. Check its file permissions. If you want to replace it, close this dialog, move or rename the item shown below, then reopen MCP settings.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(explanation, "McpConfigurationUnavailableMessage");
        var path = new TextBox
        {
            Text = viewModel.McpServerSettingsPath,
            IsReadOnly = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 56,
            Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#F2F2F2")),
            Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#171717"))
        };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(path, "McpConfigurationUnavailablePath");
        global::Avalonia.Automation.AutomationProperties.SetName(path, viewModel.McpServerSettingsPath);
        var close = new Button { Content = "Close", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        var warning = new Window
        {
            Title = "MCP configuration unavailable", Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children =
            {
                explanation,
                new TextBlock { Text = "MCP settings file", FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                path,
                close
            } }
        };
        close.Click += (_, _) => warning.Close();
        await warning.ShowDialog(this);
    }

    private void ReadingWidth_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out var width) && DataContext is ViewModels.MainViewModel viewModel)
            viewModel.SetReadingWidth(width);
    }

    private void FontFamily_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string family } && DataContext is ViewModels.MainViewModel viewModel)
            viewModel.SetUiFontFamily(family);
    }

    private void FontSize_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out var size) && DataContext is ViewModels.MainViewModel viewModel)
            viewModel.SetUiFontSize(size);
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
        if (!viewModel.IsLocalModel)
        {
            if (viewModel.Provider != Codev.CloudModelProviders.OpenAI ||
                !Codev.OpenAiGenerationSettings.SupportsReasoningControls(conversation.Model)) return;
            var primary = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(viewModel.IsDarkTheme ? "#ECECEC" : "#262522"));
            var secondary = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(viewModel.IsDarkTheme ? "#B0B0B0" : "#65625D"));
            var effort = OpenAiSetting(conversation.OpenAiReasoningEffort,
                Codev.OpenAiGenerationSettings.ReasoningEffortOptions(conversation.Model));
            var verbosity = OpenAiSetting(conversation.OpenAiVerbosity, ["low", "medium", "high"]);
            var modes = Codev.OpenAiGenerationSettings.ReasoningModeOptions(conversation.Model);
            var mode = OpenAiSetting(conversation.OpenAiReasoningMode, modes);
            var content = new StackPanel { Spacing = 12, Margin = new Thickness(20), Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(viewModel.IsDarkTheme ? "#191919" : "#F5F4F1")) };
            content.Children.Add(new TextBlock { Text = "These optional controls apply to supported GPT-5+ and o-series OpenAI models. Model default leaves the corresponding API field out of the request.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = secondary, MaxWidth = 420 });
            AddOpenAiSetting(content, "Reasoning effort", effort, "Low is faster and uses fewer reasoning tokens; high can help with harder coding tasks.", primary, secondary);
            AddOpenAiSetting(content, "Response verbosity", verbosity, "Low produces more concise answers; high gives more explanation.", primary, secondary);
            if (modes.Count > 0)
                AddOpenAiSetting(content, "Reasoning mode", mode, "Pro performs additional model work and uses more time and tokens on difficult tasks.", primary, secondary);
            var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
            var openAiDialog = new Window { Title = "OpenAI generation settings", Width = 480, Height = modes.Count > 0 ? 460 : 360, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = content.Background, Foreground = primary };
            var resetOpenAi = new Button { Content = "Use model defaults", Classes = { "soft" } };
            resetOpenAi.Click += (_, _) => { viewModel.SetOpenAiGenerationSettings(null, null, null); openAiDialog.Close(); };
            actions.Children.Add(resetOpenAi);
            actions.Children.Add(new Button { Content = "Cancel", IsCancel = true });
            var saveOpenAi = new Button { Content = "Save", IsDefault = true };
            saveOpenAi.Click += (_, _) =>
            {
                viewModel.SetOpenAiGenerationSettings(SelectedOpenAiSetting(effort), SelectedOpenAiSetting(verbosity), SelectedOpenAiSetting(mode));
                openAiDialog.Close();
            };
            actions.Children.Add(saveOpenAi);
            content.Children.Add(actions);
            openAiDialog.Content = new Border { Background = content.Background, Child = content };
            await openAiDialog.ShowDialog(this);
            return;

            static ComboBox OpenAiSetting(string? selected, IReadOnlyList<string> options)
            {
                var picker = new ComboBox
                {
                    ItemsSource = new[] { "Model default" }.Concat(options.Select(option => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(option))).ToArray(),
                    MinWidth = 180
                };
                picker.SelectedItem = selected is not null && options.Contains(selected, StringComparer.OrdinalIgnoreCase)
                    ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(selected)
                    : "Model default";
                return picker;
            }

            static void AddOpenAiSetting(StackPanel panel, string label, ComboBox picker, string hint,
                global::Avalonia.Media.IBrush primaryBrush, global::Avalonia.Media.IBrush secondaryBrush)
            {
                panel.Children.Add(new TextBlock { Text = label, FontWeight = global::Avalonia.Media.FontWeight.SemiBold, Foreground = primaryBrush });
                panel.Children.Add(new TextBlock { Text = hint, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = secondaryBrush });
                panel.Children.Add(picker);
            }

            static string? SelectedOpenAiSetting(ComboBox picker) => picker.SelectedItem is string value && value != "Model default" ? value : null;
        }
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
            var slashSearchToken = slashSearch.Token;
            var slashCaretIndex = ComposerTextBox.CaretIndex;
            try
            {
                await Task.Delay(100, slashSearchToken);
                var commands = await viewModel.GetSlashCommandSuggestionsAsync(text, slashCaretIndex, slashSearchToken);
                if (slashSearchToken.IsCancellationRequested || !ReferenceEquals(DataContext, viewModel) ||
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
        var searchToken = search.Token;
        var caretIndex = ComposerTextBox.CaretIndex;
        try
        {
            await Task.Delay(120, searchToken);
            var suggestions = await Task.Run(() => viewModel.GetProjectFileSuggestions(mention.Prefix), searchToken);
            if (searchToken.IsCancellationRequested || !ReferenceEquals(DataContext, viewModel) || ComposerTextBox.Text != text || ComposerTextBox.CaretIndex != caretIndex) return;
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
            case Codev.SlashCommandAction.ReviewLastTurn:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (await viewModel.ReviewUncommittedChangesAsync(lastTurn: true) is { } lastTurnReview)
                    await ShowReadOnlyReviewAsync(lastTurnReview, this);
                break;
            case Codev.SlashCommandAction.ReviewCommit:
            case Codev.SlashCommandAction.SecurityReviewCommit:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (await ShowCommitHashDialogAsync(this) is { Length: > 0 } commitHash &&
                    await viewModel.ReviewUncommittedChangesAsync(
                        securityFocused: command.Action == Codev.SlashCommandAction.SecurityReviewCommit,
                        commit: commitHash) is { } commitReview)
                    await ShowReadOnlyReviewAsync(commitReview, this);
                break;
            case Codev.SlashCommandAction.ReviewBranch:
            case Codev.SlashCommandAction.SecurityReviewBranch:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (await ShowBaseBranchDialogAsync(this) is { Length: > 0 } baseBranch &&
                    await viewModel.ReviewUncommittedChangesAsync(
                        securityFocused: command.Action == Codev.SlashCommandAction.SecurityReviewBranch,
                        baseBranch: baseBranch) is { } branchReview)
                    await ShowReadOnlyReviewAsync(branchReview, this);
                break;
            case Codev.SlashCommandAction.DraftPullRequestDescription:
                viewModel.Draft = "";
                ComposerTextBox.Text = "";
                if (await ShowBaseBranchDialogAsync(this) is { Length: > 0 } prBase &&
                    await DraftPullRequestDescriptionAsync(viewModel, prBase) is { } prDraft)
                    await ShowReadOnlyReviewAsync($"Pull request draft · {prBase}...HEAD\n\n{prDraft}", this);
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
            Text = $"This summarizes complete exchanges in messages {proposal.FromMessageCount + 1} through {proposal.ThroughMessageCount}. Messages outside the selected range remain verbatim in future prompts, including the latest {proposal.KeptTurns} exchange(s). The original transcript stays visible, saved, and in exports. Review or edit the summary before applying it.",
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
            Text = "Prompt templates imported from older Codev versions also appear as /template-… commands (SAVED) and insert into the composer for review. Manage templates in Settings or migrate them to Markdown command files.",
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
            Text = "A skill is a folder containing SKILL.md. It appears as /skill-name with its description in the menu. Selecting it loads the prompt into the composer for review; it is never sent automatically. In Code tasks, the model can request a skill through its restricted loader tool. Codev does not run scripts from skill folders.",
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
        content.Children.Add(new TextBlock
        {
            Text = "Also discovers shared skills in ~/.config/opencode/skills, ~/.claude/skills, and ~/.agents/skills. XDG_CONFIG_HOME and OPENCODE_CONFIG_DIR are respected for OpenCode.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Classes = { "muted" }
        });
        var openUser = new Button { Content = "Open user skills folder", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left };
        openUser.Click += async (_, _) => await OpenCommandFolderAsync(viewModel.UserSkillsFolder, viewModel);
        content.Children.Add(openUser);
        content.Children.Add(new TextBlock { Text = "Project skills · trusted project only", FontWeight = global::Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 5, 0, 0) });
        content.Children.Add(new TextBox
        {
            Text = projectFolder ?? (viewModel.HasProject ? "Trust this project to enable .codev/skills, .opencode/skills, .claude/skills, and .agents/skills." : "Attach and trust a project to use project skills."),
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
            viewModel.ReportContextActionStatus($"Opened folder · {path}");
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
            Text = "This folder is untrusted. Chat and read-only browsing remain available, and you can explicitly select files. Until you trust it, Codev keeps automatic project context and project guidance off and Code task unavailable. Trust is stored locally. Trusted folders can provide applicable AGENTS.md guidance and .codev skills and commands; project hooks and MCP settings are not used.",
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

    private async void ProjectFormatters_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || !viewModel.IsProjectTrusted ||
            viewModel.ActiveConversation?.ProjectPath is not { Length: > 0 } projectPath) return;
        string text;
        try { text = await Codev.ProjectFormatterCatalog.ReadConfigurationTextAsync(projectPath, isTrusted: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            viewModel.ReportContextActionStatus($"Could not read formatter configuration ({ex.GetType().Name}).");
            return;
        }

        var editor = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new global::Avalonia.Media.FontFamily("monospace"),
            FontSize = 12,
            MinHeight = 260,
            Watermark = Codev.ProjectFormatterCatalog.EmptyConfiguration
        };
        var status = new TextBlock { Text = "Formatters are disabled until configured. A formatter runs only after an accepted file change and still follows this project's command permission mode.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 11 };
        var save = new Button { Content = "Validate and save", Classes = { "soft" } };
        var close = new Button { Content = "Close", Classes = { "soft" } };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { save, close } };
        var layout = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 9 };
        layout.Children.Add(new TextBlock { Text = "Trusted project formatter configuration · .codev/formatters.json", FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        Grid.SetRow(editor, 1);
        layout.Children.Add(editor);
        Grid.SetRow(status, 2);
        layout.Children.Add(status);
        Grid.SetRow(buttons, 3);
        layout.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Project formatters",
            Width = 760,
            Height = Math.Min(540, Math.Max(400, Bounds.Height - 80)),
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        close.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            if (!Codev.ProjectFormatterCatalog.ValidateJson(editor.Text ?? "", out _, out var error))
            {
                status.Text = error;
                return;
            }
            try
            {
                await Codev.ProjectFormatterCatalog.SaveAsync(projectPath, editor.Text ?? "", viewModel.IsProjectTrusted);
                status.Text = "Formatter configuration saved. Formatters remain governed by this project's permission mode.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                status.Text = $"Could not save formatter configuration ({ex.GetType().Name}).";
            }
        };
        await dialog.ShowDialog(this);
    }

    private async void ProjectCommandPermissions_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || viewModel.ActiveConversation?.ProjectPath is not { Length: > 0 } projectPath) return;

        var availableHeight = Math.Clamp(Bounds.Height - 48, 320, 560);
        var header = new StackPanel { Spacing = 10 };
        header.Children.Add(new TextBlock
        {
            Text = "Command rules are saved locally for this exact project folder, outside the project. Auto approves all shell commands—including destructive commands and Git writes—and calls configured MCP tools without approval, unless an exact saved project deny rule blocks that command or MCP server/tool. Shell commands run with your account permissions and are not sandboxed to this folder; MCP tools may affect external services. To save a shell deny rule, use Ask every time, choose Deny exact command, then switch back to Auto. In Ask-every-time and Allowlist modes, commands still follow their normal approval policy. Simple read-only inspections in Read-only mode use bounded .NET file APIs instead of a shell. New private Codev workspaces start in Auto; attached project folders default to Ask every time.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        var mode = new ComboBox
        {
            ItemsSource = new[] { "Ask every time", "Auto · approve unless denied", "Allow exact saved commands", "Read-only commands" },
            SelectedIndex = viewModel.ProjectCommandPermissionMode switch
            {
                Codev.ProjectCommandPermissionMode.Auto => 1,
                Codev.ProjectCommandPermissionMode.Allowlist => 2,
                Codev.ProjectCommandPermissionMode.ReadOnly => 3,
                _ => 0
            },
            IsEnabled = viewModel.CanPersistProjectCommandPermissions
        };
        header.Children.Add(new TextBlock { Text = "Approval mode", FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        header.Children.Add(mode);
        var notice = new TextBlock
        {
            Text = viewModel.ProjectCommandPermissionStoreNotice,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
            Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush
        };
        header.Children.Add(notice);
        header.Children.Add(new TextBlock { Text = "Saved exact command rules", FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        var layout = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 8
        };
        layout.Children.Add(header);
        var rules = new StackPanel { Spacing = 6 };
        var noRules = new TextBlock { Text = "No saved command rules for this project.", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        var scroll = new ScrollViewer { Content = rules, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        var status = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 11 };
        Grid.SetRow(status, 2);
        layout.Children.Add(status);

        void RefreshRules()
        {
            rules.Children.Clear();
            var currentRules = viewModel.ProjectCommandPermissionRules;
            if (currentRules.Count == 0) { rules.Children.Add(noRules); return; }
            foreach (var rule in currentRules.OrderBy(rule => rule.Command, StringComparer.Ordinal))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = rule.Decision == Codev.ProjectCommandPermissionDecision.Allow
                        ? Codev.ProjectCommandPermissionRegistry.CanCreateAllowRule(rule.Command) ? "ALLOW" : "ASK"
                        : "DENY",
                    FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                    Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                });
                var command = new TextBlock { Text = rule.Command, TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
                ToolTip.SetTip(command, rule.Command);
                if (rule.Decision == Codev.ProjectCommandPermissionDecision.Allow && !Codev.ProjectCommandPermissionRegistry.CanCreateAllowRule(rule.Command))
                    ToolTip.SetTip(command, "This saved rule cannot skip approval because it invokes Git or references protected .git or Codev app data.\n\n" + rule.Command);
                Grid.SetColumn(command, 1);
                row.Children.Add(command);
                var remove = new Button { Content = "Remove", Classes = { "soft" }, IsEnabled = viewModel.CanPersistProjectCommandPermissions };
                remove.Click += async (_, _) =>
                {
                    try { await viewModel.RemoveProjectCommandPermissionRuleAsync(rule.Command, rule.Decision); RefreshRules(); status.Text = "Rule removed."; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { status.Text = $"Could not remove rule ({ex.GetType().Name})."; }
                };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                rules.Children.Add(row);
            }
        }
        RefreshRules();
        mode.SelectionChanged += async (_, _) =>
        {
            try
            {
                var selectedMode = mode.SelectedIndex switch
                {
                    1 => Codev.ProjectCommandPermissionMode.Auto,
                    2 => Codev.ProjectCommandPermissionMode.Allowlist,
                    3 => Codev.ProjectCommandPermissionMode.ReadOnly,
                    _ => Codev.ProjectCommandPermissionMode.AskEveryTime
                };
                await viewModel.SetProjectCommandPermissionModeAsync(selectedMode);
                status.Text = "Permission mode saved.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                status.Text = $"Could not save the mode ({ex.GetType().Name}).";
                mode.SelectedIndex = viewModel.ProjectCommandPermissionMode switch
                {
                    Codev.ProjectCommandPermissionMode.Auto => 1,
                    Codev.ProjectCommandPermissionMode.Allowlist => 2,
                    Codev.ProjectCommandPermissionMode.ReadOnly => 3,
                    _ => 0
                };
            }
        };

        var close = new Button { Content = "Close", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        Grid.SetRow(close, 3);
        close.Margin = new Thickness(0, 4, 0, 0);
        layout.Children.Add(close);
        var dialog = new Window
        {
            Title = "Command permissions · " + Path.GetFileName(projectPath),
            Width = 700,
            Height = availableHeight,
            MaxHeight = availableHeight,
            MinWidth = 560,
            MinHeight = Math.Min(360, availableHeight),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void ReviewFileChanges_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel || !viewModel.CanReviewFileChanges || viewModel.ActiveConversation is not { } conversation) return;
        var entries = conversation.FileChanges.OrderByDescending(change => change.ChangedAt).ToArray();
        var historyNotice = Codev.ConversationFileChangeHistoryService.GetStatusMessage(conversation);
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
                    new TextBlock { Text = $"{entries.Length} change record(s) across {entries.Select(change => change.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} file(s). Newest first. Restores show the complete replacement and save the current file as a new checkpoint." + (historyNotice is null ? "" : "\n" + historyNotice), TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush },
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
            var restoredExists = File.Exists(files.ResolvePath(change.RelativePath));
            var restored = restoredExists ? await files.ReadFileSnapshotAsync(change.RelativePath) : null;
            Codev.ConversationFileChangeHistoryService.Record(conversation, new Codev.FileChangeRecord(change.RelativePath, rollback, DateTimeOffset.Now, "Restore", currentExists,
                ResultFileExisted: restoredExists, ResultSha256: restored?.Sha256));
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
        var discardAll = new Button { Content = "Discard all unstaged…", Classes = { "soft" }, IsEnabled = false };
        ToolTip.SetTip(discardAll, "Preview and discard all unstaged and untracked changes; staged changes are preserved");
        branchRow.Children.Add(switchBranch);
        branchRow.Children.Add(createBranch);
        branchRow.Children.Add(refresh);
        branchRow.Children.Add(discardAll);
        header.Children.Add(branchRow);
        var summary = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush };
        header.Children.Add(summary);
        var activeConversation = viewModel.ActiveConversation;
        var childWorktreeManager = viewModel.ChildWorktreeManager;
        Codev.Conversation[] childChoices = activeConversation is null ? [] : activeConversation.ParentConversationId is not null
            ? new[] { activeConversation }
            : activeConversation.ChildConversations.ToArray();
        childChoices = childChoices.Where(child => child.ChildWorktreeBranch is not null && child.ChildWorktreeStartCommit is not null).ToArray();
        var childReviewRow = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 8, IsVisible = childChoices.Length > 0 };
        childReviewRow.Children.Add(new TextBlock { Text = "Child worktree", VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center });
        var childPicker = new ComboBox { MinWidth = 280, ItemsSource = childChoices.Select(child => new ChildWorktreeChoice(child)).ToArray() };
        childPicker.SelectedIndex = childChoices.Length > 0 ? 0 : -1;
        var reviewChild = new Button { Content = "Review child diff…", Classes = { "soft" }, IsEnabled = childChoices.Length > 0 };
        var recoverChild = new Button { Content = "Recover child worktree…", Classes = { "soft" }, IsEnabled = false };
        childReviewRow.Children.Add(childPicker);
        childReviewRow.Children.Add(reviewChild);
        childReviewRow.Children.Add(recoverChild);
        header.Children.Add(childReviewRow);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*"), ColumnSpacing = 10, Margin = new Thickness(0, 14, 0, 12) };
        var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var files = new ListBox();
        var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 8, 0, 0) };
        var stage = new Button { Content = "Stage selected", Classes = { "soft" }, IsEnabled = false };
        var bulkStage = new Button { Content = "Stage all", Classes = { "soft" }, IsEnabled = false };
        var commit = new Button { Content = "Review staged diff & commit…", Classes = { "soft" }, IsEnabled = false };
        actions.Children.Add(stage);
        actions.Children.Add(bulkStage);
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
        var stageHunk = new Button { Content = "Stage hunk", Classes = { "soft" }, IsVisible = false, IsEnabled = false };
        var unstageHunk = new Button { Content = "Unstage hunk", Classes = { "soft" }, IsVisible = false, IsEnabled = false };
        var revertHunk = new Button { Content = "Revert hunk", Classes = { "soft" }, IsVisible = false, IsEnabled = false };
        var revertFile = new Button { Content = "Revert file", Classes = { "soft" }, IsVisible = false, IsEnabled = false };
        ToolTip.SetTip(stageHunk, "Stage only the selected diff hunk");
        ToolTip.SetTip(unstageHunk, "Unstage only the selected diff hunk");
        ToolTip.SetTip(revertHunk, "Revert only the selected unstaged diff hunk");
        ToolTip.SetTip(revertFile, "Discard all unstaged changes for this file; staged changes are preserved");
        var askAboutDiff = new Button { Content = "Ask Codev about selection", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), IsEnabled = false };
        ToolTip.SetTip(askAboutDiff, "Add selected diff lines to the composer without sending them");
        var commentOnDiff = new Button { Content = "Comment on selection", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), IsEnabled = false };
        ToolTip.SetTip(commentOnDiff, "Attach a review comment to selected diff lines; it will not be sent until you send a prompt");
        var diffActions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { stageHunk, unstageHunk, revertHunk, revertFile, askAboutDiff, commentOnDiff } };
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
        void UpdateHunkActions()
        {
            revertFile.IsVisible = revertFile.IsEnabled = files.SelectedItem is ListBoxItem { Tag: Codev.GitFileStatus revertableFile } &&
                revertableFile.WorkingTree != " " && revertableFile.OriginalPath is null && !revertableFile.State.Contains('U') &&
                !string.IsNullOrWhiteSpace(diffBox.Text) &&
                !diffBox.Text.StartsWith("Loading diff", StringComparison.Ordinal) &&
                !diffBox.Text.StartsWith("Could not load diff", StringComparison.Ordinal) &&
                !diffBox.Text.StartsWith("Git reported no textual diff", StringComparison.Ordinal);
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus selectedFile } ||
                !Codev.GitDiffHunkSelector.TrySelect(diffBox.Text ?? "", diffBox.SelectionStart,
                    diffBox.SelectionEnd - diffBox.SelectionStart, out var selection) || selection is null)
            {
                stageHunk.IsVisible = unstageHunk.IsVisible = revertHunk.IsVisible = false;
                stageHunk.IsEnabled = unstageHunk.IsEnabled = revertHunk.IsEnabled = false;
                return;
            }

            stageHunk.IsVisible = selection.Section == Codev.GitDiffHunkSection.Unstaged && selectedFile.WorkingTree != " ";
            stageHunk.IsEnabled = stageHunk.IsVisible;
            unstageHunk.IsVisible = selection.Section == Codev.GitDiffHunkSection.Staged && selectedFile.Staged != " ";
            unstageHunk.IsEnabled = unstageHunk.IsVisible;
            revertHunk.IsVisible = selection.Section == Codev.GitDiffHunkSection.Unstaged && selectedFile.WorkingTree != " ";
            revertHunk.IsEnabled = revertHunk.IsVisible;
        }

        async Task ApplySelectedHunkAsync(Codev.GitDiffHunkAction action, string title)
        {
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus file } ||
                !Codev.GitDiffHunkSelector.TrySelect(diffBox.Text, diffBox.SelectionStart,
                    diffBox.SelectionEnd - diffBox.SelectionStart, out _)) return;
            stageHunk.IsEnabled = unstageHunk.IsEnabled = revertHunk.IsEnabled = false;
            try
            {
                await service.ApplyHunkAsync(file.Path, diffBox.Text ?? "", diffBox.SelectionStart,
                    diffBox.SelectionEnd - diffBox.SelectionStart, action);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                await ShowGitInfoAsync(title, ex.Message, dialog);
                await RefreshAsync();
            }
        }

        reviewChild.Click += async (_, _) =>
        {
            if (childPicker.SelectedItem is not ChildWorktreeChoice choice || choice.Conversation.ChildWorktreeBranch is not { } childBranch ||
                choice.Conversation.ChildWorktreeStartCommit is not { } startCommit) return;
            await ReviewChildWorktreeAsync(childWorktreeManager, status.Root,
                choice.Conversation, childBranch, startCommit, dialog, RefreshAsync);
        };
        void UpdateChildActions()
        {
            var selected = (childPicker.SelectedItem as ChildWorktreeChoice)?.Conversation;
            reviewChild.IsEnabled = selected is not null && childWorktreeManager.IsManagedWorktreePath(selected.ProjectPath ?? "");
            recoverChild.IsEnabled = selected is not null && !childWorktreeManager.IsManagedWorktreePath(selected.ProjectPath ?? "") &&
                selected.ParentConversationId is not null && selected.ChildWorktreeBranch is not null && selected.ChildWorktreeStartCommit is not null;
        }
        childPicker.SelectionChanged += (_, _) => UpdateChildActions();
        recoverChild.Click += async (_, _) =>
        {
            if (childPicker.SelectedItem is not ChildWorktreeChoice choice) return;
            recoverChild.IsEnabled = false;
            if (await viewModel.RecoverChildWorktreeAsync(choice.Conversation))
            {
                await ShowGitInfoAsync("Child worktree recovered", "The isolated checkout was restored from its saved branch. You can reopen the child conversation and continue.", dialog);
                UpdateChildActions();
            }
            else
                await ShowGitInfoAsync("Could not recover child worktree", viewModel.ContextActionStatus, dialog);
            UpdateChildActions();
        };
        UpdateChildActions();

        void RenderStatus(Codev.GitRepositoryStatus value)
        {
            var tracking = value.Upstream is null ? "no upstream" : value.Upstream + (value.Ahead > 0 || value.Behind > 0 ? $" · ahead {value.Ahead}, behind {value.Behind}" : " · up to date");
            summary.Text = value.HasChanges ? $"{value.Files.Count} changed file(s) · {tracking}" : $"Working tree clean · {tracking}";
            files.Items.Clear();
            stage.IsEnabled = false;
            bulkStage.IsEnabled = false;
            diffBox.Text = value.HasChanges ? "Select a changed file to inspect its staged and unstaged diff." : "The working tree is clean.";
            UpdateHunkActions();
            if (!value.HasChanges) files.Items.Add(new ListBoxItem { Content = "No staged, unstaged, or untracked changes.", IsEnabled = false });
            foreach (var file in value.Files)
                files.Items.Add(new ListBoxItem
                {
                    Content = $"{file.State.Replace(' ', '·')}   {file.DisplayPath}",
                    Tag = file,
                    FontFamily = new global::Avalonia.Media.FontFamily("Consolas")
                });
            commit.IsEnabled = value.Files.Any(file => file.Staged != " ");
            var hasUnstaged = value.Files.Any(file => file.WorkingTree != " ");
            var hasStaged = value.Files.Any(file => file.Staged != " ");
            bulkStage.Content = hasUnstaged ? "Stage all" : "Unstage all";
            bulkStage.IsEnabled = hasUnstaged || hasStaged;
            discardAll.IsEnabled = hasUnstaged;
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
                bulkStage.IsEnabled = false;
                discardAll.IsEnabled = false;
                commit.IsEnabled = false;
                diffBox.Text = "Git status could not be loaded.";
                UpdateHunkActions();
            }
        }

        files.SelectionChanged += async (_, _) =>
        {
            var revision = ++diffRevision;
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus file })
            {
                stage.IsEnabled = false;
                askAboutDiff.IsEnabled = false;
                commentOnDiff.IsEnabled = false;
                UpdateHunkActions();
                return;
            }
            askAboutDiff.IsEnabled = false;
            commentOnDiff.IsEnabled = false;
            stage.Content = file.Staged != " " ? "Unstage file" : "Stage file";
            stage.IsEnabled = true;
            diffBox.SelectionStart = 0;
            diffBox.SelectionEnd = 0;
            diffBox.Text = "Loading diff…";
            UpdateHunkActions();
            try
            {
                var diff = await service.GetFileDiffAsync(file);
                if (revision == diffRevision)
                {
                    diffBox.Text = string.IsNullOrWhiteSpace(diff) ? "Git reported no textual diff for this file (it may be binary or unchanged since status was refreshed)." : diff;
                    UpdateHunkActions();
                }
            }
            catch (Exception ex)
            {
                if (revision == diffRevision)
                {
                    diffBox.Text = "Could not load diff: " + ex.Message;
                    UpdateHunkActions();
                }
            }
        };
        diffBox.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name is "SelectionStart" or "SelectionEnd")
            {
                var hasSelection = files.SelectedItem is ListBoxItem { Tag: Codev.GitFileStatus } && !string.IsNullOrWhiteSpace(diffBox.SelectedText);
                askAboutDiff.IsEnabled = hasSelection;
                commentOnDiff.IsEnabled = hasSelection;
                UpdateHunkActions();
            }
        };
        stageHunk.Click += async (_, _) => await ApplySelectedHunkAsync(Codev.GitDiffHunkAction.Stage, "Could not stage hunk");
        unstageHunk.Click += async (_, _) => await ApplySelectedHunkAsync(Codev.GitDiffHunkAction.Unstage, "Could not unstage hunk");
        revertHunk.Click += async (_, _) =>
        {
            if (!await ConfirmGitActionAsync(dialog, "Revert selected hunk?", "Only the selected unstaged hunk will be reversed. This cannot be undone.")) return;
            await ApplySelectedHunkAsync(Codev.GitDiffHunkAction.Revert, "Could not revert hunk");
        };
        revertFile.Click += async (_, _) =>
        {
            if (files.SelectedItem is not ListBoxItem { Tag: Codev.GitFileStatus file } || file.WorkingTree == " ") return;
            var explanation = file.WorkingTree == "?"
                ? $"Permanently delete the untracked file {file.DisplayPath}? This cannot be undone."
                : $"Discard all unstaged changes to {file.DisplayPath}? Any staged changes will remain. This cannot be undone.";
            if (!await ConfirmGitActionAsync(dialog, file.WorkingTree == "?" ? "Delete untracked file?" : "Revert entire file?", explanation)) return;
            revertFile.IsEnabled = false;
            try
            {
                await service.RevertFileAsync(file.Path, diffBox.Text ?? "");
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                await ShowGitInfoAsync("Could not revert file", ex.Message, dialog);
                await RefreshAsync();
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
                if (file.Staged != " ") await service.UnstageFileAsync(file.Path);
                else await service.StageFileAsync(file.Path);
                await RefreshAsync();
            }
            catch (Exception ex) { await ShowGitInfoAsync("Could not update the Git index", ex.Message, dialog); await RefreshAsync(); }
        };
        bulkStage.Click += async (_, _) =>
        {
            var hasUnstaged = status.Files.Any(file => file.WorkingTree != " ");
            bulkStage.IsEnabled = false;
            try
            {
                if (hasUnstaged) await service.StageAllAsync();
                else await service.UnstageAllAsync();
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                await ShowGitInfoAsync(hasUnstaged ? "Could not stage all changes" : "Could not unstage all changes", ex.Message, dialog);
                await RefreshAsync();
            }
        };
        discardAll.Click += async (_, _) =>
        {
            discardAll.IsEnabled = false;
            try
            {
                var preview = await service.GetUnstagedDiscardPreviewAsync();
                var details = string.Join(Environment.NewLine + Environment.NewLine,
                    preview.Changes.Select(change => $"{change.File.State.Replace(' ', '·')}   {change.File.DisplayPath}{Environment.NewLine}{change.DisplayedDiff}"));
                var previewContent = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
                previewContent.Children.Add(new TextBlock
                {
                    Text = $"Review all {preview.Changes.Count} unstaged path(s). Confirming permanently discards these working-tree edits and removes listed untracked files. Staged changes are preserved. This cannot be undone.",
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
                });
                var previewText = new TextBox
                {
                    Text = details,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
                    FontFamily = new global::Avalonia.Media.FontFamily("Consolas"),
                    FontSize = viewModel.UiFontSize,
                    Padding = new Thickness(10),
                    Background = this.FindResource("SurfaceBrush") as global::Avalonia.Media.IBrush,
                    Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
                };
                var previewScroll = new ScrollViewer { Content = previewText, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
                Grid.SetRow(previewScroll, 1);
                previewContent.Children.Add(previewScroll);
                var previewActions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
                var cancelDiscard = new Button { Content = "Cancel", Classes = { "soft" } };
                var confirmDiscard = new Button { Content = "Discard all unstaged changes", Classes = { "soft" } };
                previewActions.Children.Add(cancelDiscard);
                previewActions.Children.Add(confirmDiscard);
                Grid.SetRow(previewActions, 2);
                previewContent.Children.Add(previewActions);
                var previewWindow = new Window
                {
                    Title = "Review unstaged changes",
                    Width = 850,
                    Height = 620,
                    MinWidth = 600,
                    MinHeight = 400,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = previewContent
                };
                cancelDiscard.Click += (_, _) => previewWindow.Close(false);
                confirmDiscard.Click += (_, _) => previewWindow.Close(true);
                if (await previewWindow.ShowDialog<bool>(dialog) != true) return;
                await service.RevertAllUnstagedAsync(preview);
            }
            catch (Exception ex)
            {
                await ShowGitInfoAsync("Could not discard all unstaged changes", ex.Message, dialog);
            }
            finally { await RefreshAsync(); }
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
        var draftMessage = new Button { Content = "Draft with local model", Classes = { "soft" } };
        var commit = new Button { Content = "Create local commit", Classes = { "soft" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(draftMessage);
        buttons.Children.Add(commit);
        layout.Children.Add(buttons);
        var dialog = new Window { Title = "Review staged changes", Width = 900, Height = 620, MinWidth = 640, MinHeight = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        using var draftCancellation = new CancellationTokenSource();
        dialog.Closed += (_, _) => draftCancellation.Cancel();
        cancel.Click += (_, _) => dialog.Close();
        draftMessage.Click += async (_, _) =>
        {
            if (DataContext is not ViewModels.MainViewModel viewModel) return;
            draftMessage.IsEnabled = false;
            draftMessage.Content = "Drafting…";
            try
            {
                message.Text = await viewModel.DraftCommitMessageAsync(stagedReview, draftCancellation.Token);
                message.CaretIndex = message.Text?.Length ?? 0;
                message.Focus();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await ShowGitInfoAsync("Could not draft commit message", ex.Message, dialog); }
            finally
            {
                draftMessage.Content = "Draft with local model";
                draftMessage.IsEnabled = true;
            }
        };
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

    private async Task ReviewChildWorktreeAsync(Codev.GitChildWorktreeManager manager, string repositoryRoot,
        Codev.Conversation child, string branch, string startCommit, Window owner, Func<Task> refresh)
    {
        Codev.GitChildWorktreeReview review;
        try { review = await manager.GetReviewAsync(repositoryRoot, branch, startCommit); }
        catch (Exception ex) { await ShowGitInfoAsync("Could not review child worktree", ex.Message, owner); return; }

        var layout = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10 };
        var note = new TextBlock
        {
            Text = $"Reviewing {child.Title} · {review.Branch} against its recorded start commit. The target branch is the currently checked out branch ({review.BaseBranch})." +
                   (review.HasUncommittedChanges ? " The child also has uncommitted changes; commit them in the child conversation and refresh before merging." : ""),
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };
        layout.Children.Add(note);
        var diffBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(review.Diff) ? "No committed child changes." : review.Diff,
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
        layout.Children.Add(new ScrollViewer { Content = diffBox, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var close = new Button { Content = "Close", Classes = { "soft" } };
        var merge = new Button { Content = "Merge into current branch", Classes = { "soft" }, IsEnabled = !review.Truncated && !review.HasUncommittedChanges && review.Files.Count > 0 };
        actions.Children.Add(close);
        actions.Children.Add(merge);
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        var dialog = new Window { Title = "Review child worktree", Width = 980, Height = 680, MinWidth = 680, MinHeight = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        close.Click += (_, _) => dialog.Close();
        merge.Click += async (_, _) =>
        {
            var warning = review.Truncated
                ? "The diff is truncated. Reduce the child changes before merging."
                : $"Merge the reviewed changes from {review.Branch} into {review.BaseBranch}? This creates a local merge commit and does not push.";
            if (!await ConfirmGitActionAsync(dialog, "Merge child worktree?", warning)) return;
            merge.IsEnabled = false;
            try
            {
                await manager.MergeAsync(repositoryRoot, review);
                dialog.Close();
                await refresh();
            }
            catch (Exception ex)
            {
                merge.IsEnabled = true;
                await ShowGitInfoAsync("Could not merge child worktree", ex.Message, dialog);
            }
        };
        await dialog.ShowDialog(owner);
    }

    private sealed record ChildWorktreeChoice(Codev.Conversation Conversation)
    {
        public string Label => $"{Conversation.Title} · {Conversation.ChildWorktreeBranch}";
        public override string ToString() => Label;
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
            new TextBlock { Text = "Read-only Git review · second opinion", FontSize = 15, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            new TextBlock { Text = "The review used a bounded local Git diff. No project files or Git state were changed.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
            text,
            close
        }};
        var dialog = new Window { Title = "Git review", Width = 760, Height = 620, MinWidth = 560, MinHeight = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    private async Task<string?> ShowCommitHashDialogAsync(Window owner)
    {
        var hash = new TextBox { Watermark = "Commit hash (7–40 hexadecimal characters)", MinWidth = 360 };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" }, IsCancel = true };
        var review = new Button { Content = "Review", Classes = { "soft" }, IsDefault = true };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, review } };
        var dialog = new Window
        {
            Title = "Review a commit", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = owner.FindResource("AppBackgroundBrush") as global::Avalonia.Media.IBrush,
            Foreground = owner.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children =
            {
                new TextBlock { Text = "Enter a commit hash from this repository. The bounded diff stays local and is reviewed by the selected local model, if available.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                hash, buttons
            }}
        };
        cancel.Click += (_, _) => dialog.Close(null);
        review.Click += (_, _) => dialog.Close(hash.Text?.Trim());
        dialog.Opened += (_, _) => hash.Focus();
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task<string?> ShowBaseBranchDialogAsync(Window owner)
    {
        var branch = new TextBox { Watermark = "Local base branch name", MinWidth = 360 };
        var cancel = new Button { Content = "Cancel", Classes = { "soft" }, IsCancel = true };
        var review = new Button { Content = "Review", Classes = { "soft" }, IsDefault = true };
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, review } };
        var dialog = new Window
        {
            Title = "Review branch changes", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = owner.FindResource("AppBackgroundBrush") as global::Avalonia.Media.IBrush,
            Foreground = owner.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children =
            {
                new TextBlock { Text = "Enter an existing local base branch. Codev compares its merge base with the current HEAD using a bounded diff.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                branch, buttons
            }}
        };
        cancel.Click += (_, _) => dialog.Close(null);
        review.Click += (_, _) => dialog.Close(branch.Text?.Trim());
        dialog.Opened += (_, _) => branch.Focus();
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task<string?> DraftPullRequestDescriptionAsync(ViewModels.MainViewModel viewModel, string baseBranch)
    {
        try { return await viewModel.DraftPullRequestDescriptionAsync(baseBranch); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            await ShowGitInfoAsync("Could not draft pull request description", ex.Message, this);
            return null;
        }
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

    private async void CreateChildSession_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && GetMenuConversation(menuItem) is { } conversation && DataContext is ViewModels.MainViewModel viewModel)
            await viewModel.CreateIsolatedChildSessionAsync(conversation);
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
        if (!(_conversationBackupPicker?.CanSave ?? StorageProvider.CanSave))
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local save dialog.");
            return;
        }
        try
        {
            var options = new FilePickerSaveOptions
            {
                Title = "Export all Codev conversations",
                SuggestedFileName = $"codev-backup-{DateTime.Now:yyyy-MM-dd}.codev.json",
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Codev conversation backup") { Patterns = ["*.codev.json", "*.json"] }]
            };
            var file = _conversationBackupPicker is null
                ? await StorageProvider.SaveFilePickerAsync(options) is { } nativeFile
                    ? new AvaloniaConversationBackupFile(nativeFile)
                    : null
                : await _conversationBackupPicker.SaveFilePickerAsync(options);
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
        if (!(_conversationBackupPicker?.CanOpen ?? StorageProvider.CanOpen))
        {
            viewModel.ReportContextActionStatus("This platform does not provide a local file picker.");
            return;
        }
        try
        {
            var options = new FilePickerOpenOptions
            {
                Title = "Import Codev conversation backup",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Codev conversation backup") { Patterns = ["*.codev.json", "*.json"] }]
            };
            IReadOnlyList<IConversationBackupFile> files;
            if (_conversationBackupPicker is null)
            {
                var nativeFiles = await StorageProvider.OpenFilePickerAsync(options);
                files = nativeFiles.Select(file => (IConversationBackupFile)new AvaloniaConversationBackupFile(file)).ToArray();
            }
            else
            {
                files = await _conversationBackupPicker.OpenFilePickerAsync(options);
            }
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

    private static async Task WriteTextFileAsync(IConversationBackupFile file, string contents)
    {
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(contents);
    }

    private static Task WriteTextFileAsync(IStorageFile file, string contents) =>
        WriteTextFileAsync(new AvaloniaConversationBackupFile(file), contents);

    private static async Task<string> ReadTextFileAsync(IConversationBackupFile file, int maxBytes)
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

    private static Task<string> ReadTextFileAsync(IStorageFile file, int maxBytes) =>
        ReadTextFileAsync(new AvaloniaConversationBackupFile(file), maxBytes);

    private static string SafeExportName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(title.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "codev-conversation" : cleaned;
    }

    private void OpenConversationFind_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        viewModel.OpenConversationFind();
        ConversationFindTextBox.Focus();
    }

    private void CloseConversationFind_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) viewModel.CloseConversationFind();
    }

    private void ClearConversationFind_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        viewModel.ConversationFindQuery = "";
        ConversationFindTextBox.Focus();
    }

    private void ConversationFindResult_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel ||
            sender is not Button { DataContext: Codev.ConversationMessageMatch match } ||
            match.MessageIndex < 0 || match.MessageIndex >= viewModel.Messages.Count) return;

        _followOutput = false;
        viewModel.CloseConversationFind();
        var approximateOffset = ConversationScrollViewer.Extent.Height * match.MessageIndex / Math.Max(1, viewModel.Messages.Count);
        ConversationScrollViewer.Offset = new global::Avalonia.Vector(ConversationScrollViewer.Offset.X, approximateOffset);
        ConversationScrollViewer.UpdateLayout();
        MessageList.ScrollIntoView(match.MessageIndex);
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var primaryModifier = PlatformKeyboardShortcuts.HasPrimaryModifier(e.KeyModifiers);
        if (e.Key == Key.F1)
            ShowKeyboardShortcuts();
        else if (primaryModifier && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.F)
        {
            viewModel.OpenConversationFind();
            ConversationFindTextBox.Focus();
        }
        else if (primaryModifier && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.M)
            viewModel.CycleConversationMode();
        else if (primaryModifier && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.A)
            viewModel.CyclePrimaryAgent();
        else if (primaryModifier && e.Key == Key.N)
            viewModel.NewConversationCommand.Execute(null);
        else if (primaryModifier && e.Key == Key.F)
            SearchTextBox.Focus(NavigationMethod.Tab, e.KeyModifiers);
        else if (primaryModifier && e.Key == Key.L)
            ComposerTextBox.Focus();
        else if (e.Key == Key.Escape && viewModel.IsConversationFindOpen)
            viewModel.CloseConversationFind();
        else if (e.Key == Key.Escape && viewModel.IsGenerating)
            viewModel.StopGenerationCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }

    private void ShowKeyboardShortcuts()
    {
        if (_keyboardShortcutsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var dialog = new Window
        {
            Title = "Keyboard shortcuts",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            MinWidth = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        _keyboardShortcutsWindow = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_keyboardShortcutsWindow, dialog)) _keyboardShortcutsWindow = null;
        };

        var shortcuts = new StackPanel { Spacing = 8 };
        var modifier = PlatformKeyboardShortcuts.PrimaryModifierLabel;
        foreach (var shortcut in new[]
        {
            $"{modifier}+N  New conversation",
            $"{modifier}+F  Search conversations",
            $"{modifier}+Shift+F  Find in this conversation",
            $"{modifier}+L  Focus the composer",
            $"{modifier}+Shift+M  Cycle Chat, Plan, and Code task",
            $"{modifier}+Shift+A  Cycle the primary agent",
            "Esc  Stop the active response",
            "F1  Show these shortcuts",
            "/status  Show local conversation and project status"
        })
            shortcuts.Children.Add(new TextBlock { Text = shortcut, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });

        var close = new Button { Content = "Close", Classes = { "soft" }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { shortcuts, close }
        };
        dialog.Show(this);
        dialog.Activate();
    }
}

using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;
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

    public MainWindow()
    {
        InitializeComponent();
        ComposerTextBox.AddHandler(InputElement.KeyDownEvent, Composer_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closed += (_, _) => _fileMentionSearch?.Cancel();
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

    private void ConfigureAgentInteractions()
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        viewModel.ReviewFileChangeAsync = ReviewAgentFileChangeAsync;
        viewModel.ApproveProjectCommandAsync = ApproveAgentCommandAsync;
        viewModel.ConfirmRepeatedToolCallAsync = ConfirmRepeatedToolCallAsync;
    }

    private async Task<bool> ReviewAgentFileChangeAsync(string relativePath, string before, string after, bool isNewFile)
    {
        var layout = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        layout.Children.Add(new TextBlock
        {
            Text = isNewFile
                ? $"The model proposes creating {relativePath}. Review the complete file before approving."
                : $"The model proposes replacing {relativePath}. Review both versions before approving; Codev will save a local checkpoint first.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var oldPane = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 6, 0) };
        var newPane = new StackPanel { Spacing = 5, Margin = new Thickness(6, 0, 0, 0) };
        oldPane.Children.Add(new TextBlock { Text = isNewFile ? "CURRENT · new file" : "CURRENT", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
        newPane.Children.Add(new TextBlock { Text = "PROPOSED", Foreground = this.FindResource("MutedTextBrush") as global::Avalonia.Media.IBrush });
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
        layout.Children.Add(panes);
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window
        {
            Title = isNewFile ? "Review new project file" : "Review project change",
            Width = 1000,
            Height = 650,
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
        layout.Children.Add(buttons);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ApproveAgentCommandAsync(string command, string projectPath, string shellName)
    {
        var layout = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        layout.Children.Add(new TextBlock
        {
            Text = $"This command runs through {shellName} with your account permissions. It can access files and services available to your account; Codev cannot sandbox it to the project folder. Review the exact command before approving.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        });
        layout.Children.Add(new TextBlock { Text = "Working directory: " + projectPath, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontWeight = global::Avalonia.Media.FontWeight.SemiBold });
        layout.Children.Add(new TextBox
        {
            Text = command, IsReadOnly = true, AcceptsReturn = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 220, FontFamily = new global::Avalonia.Media.FontFamily("Consolas"), Background = this.FindResource("ComposerBrush") as global::Avalonia.Media.IBrush,
            Foreground = this.FindResource("PrimaryTextBrush") as global::Avalonia.Media.IBrush
        });
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window { Title = "Approve project command", Width = 720, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        var reject = new Button { Content = "Cancel" };
        var approve = new Button { Content = "Approve & run" };
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

    private async void ConfigureCloudProvider_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var provider = new ComboBox { ItemsSource = new[] { "OpenAI", "Anthropic" }, SelectedIndex = 0, MinWidth = 180 };
        var apiKey = new TextBox { MinWidth = 360, PasswordChar = '•', Watermark = "Paste API key (or leave blank to use the environment variable)" };
        var acknowledgement = new CheckBox
        {
            Content = "I understand that prompts, conversation history, and selected project context will be sent to the provider and API usage may incur separate charges.",
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
        var disable = new Button { Content = "Disable hosted requests", MinWidth = 170 };
        var cancel = new Button { Content = "Close", MinWidth = 80 };
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { disable, cancel, connect }
        };
        var dialog = new Window
        {
            Title = "Connect hosted models",
            Width = 500,
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
                    new TextBlock { Text = "API key · used only in this Codev session" },
                    apiKey,
                    new TextBlock
                    {
                        Text = "Set OPENAI_API_KEY or ANTHROPIC_API_KEY to avoid pasting a key each launch. Codev does not write keys to conversation history or settings files.",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        MaxWidth = 440
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
            viewModel.IncludeProjectContextForHosted = includeProjectContext.IsChecked == true;
            var connected = await viewModel.ConnectCloudProviderAsync(providerId, apiKey.Text, acknowledgement.IsChecked == true);
            status.Text = viewModel.ConnectionStatus;
            if (connected) dialog.Close();
        };
        disable.Click += (_, _) => { viewModel.DisableCloudProviders(); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void Composer_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _fileMentionSearch?.Cancel();
        _fileMentionSearch?.Dispose();
        _fileMentionSearch = null;
        var text = ComposerTextBox.Text ?? "";
        if (DataContext is not ViewModels.MainViewModel viewModel ||
            !Codev.ProjectFileMentionParser.TryGet(text, ComposerTextBox.CaretIndex, out var mention))
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

    private void FileMentionSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string path }) ApplyProjectFileMention(path);
    }

    private void ApplyProjectFileMention(string path)
    {
        var currentText = ComposerTextBox.Text ?? "";
        if (_activeFileMention is null || DataContext is not ViewModels.MainViewModel viewModel ||
            !Codev.ProjectFileMentionParser.TryGet(currentText, ComposerTextBox.CaretIndex, out var currentMention) ||
            currentMention.StartIndex != _activeFileMention.StartIndex || !viewModel.AddProjectFileMention(path))
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
        if (primaryModifier && e.Key == Key.N)
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

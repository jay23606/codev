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

    public MainWindow()
    {
        InitializeComponent();
        ComposerTextBox.AddHandler(InputElement.KeyDownEvent, Composer_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        DataContextChanged += (_, _) => ObserveMessages();
        ObserveMessages();
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

    private void Composer_KeyDown(object? sender, KeyEventArgs e)
    {
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            viewModel.ReportContextActionStatus($"Could not attach project folder: {ex.Message}");
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

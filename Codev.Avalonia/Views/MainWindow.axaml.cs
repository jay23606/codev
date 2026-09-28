using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.IO;

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

using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.Diagnostics;
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
        viewModel.ConfirmConversationRewindAsync = ConfirmConversationRewindAsync;
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

    private async Task<bool> ReviewAgentFileChangeAsync(string relativePath, string before, string after, bool isNewFile, string? proposedPatch)
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

    private async Task<bool> ApproveAgentCommandAsync(string command, string projectPath, string shellName, bool isVerification)
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
        var apiKey = new TextBox { MinWidth = 360, PasswordChar = '•', Watermark = "Paste API key (or leave blank to use the environment variable)" };
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
            var connected = await viewModel.ConnectCloudProviderAsync(providerId, apiKey.Text, acknowledgement.IsChecked == true);
            status.Text = viewModel.ConnectionStatus;
            if (connected)
            {
                viewModel.IncludeProjectContextForHosted = includeProjectContext.IsChecked == true;
                dialog.Close();
            }
        };
        disable.Click += (_, _) => { viewModel.DisableCloudProviders(); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
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
                else viewModel.ReportContextActionStatus("Code task needs a trusted project, loopback Ollama, and no active response. Check the project trust and selected model first.");
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
            case Codev.SlashCommandAction.InitProject:
            case Codev.SlashCommandAction.ReviewProject:
                viewModel.Draft = command.Prompt ?? "";
                ComposerTextBox.Text = viewModel.Draft;
                ComposerTextBox.CaretIndex = ComposerTextBox.Text?.Length ?? 0;
                ComposerTextBox.Focus();
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

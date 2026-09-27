using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Codev;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ObservableCollection<Conversation> _conversations = [];
    private readonly List<ModelOption> _models = [];
    private Conversation? _active;
    private string? _projectPath;
    private CancellationTokenSource? _requestCancellation;
    private bool _loadingModel;

    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "conversations.json");

    public MainWindow()
    {
        InitializeComponent();
        LoadConversations();
        RefreshConversationLists();
        if (_conversations.Count > 0) SelectConversation(_conversations.OrderByDescending(c => c.UpdatedAt).First());
        Loaded += async (_, _) => await LoadModelsAsync();
    }

    private void LoadConversations()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var saved = JsonSerializer.Deserialize<List<Conversation>>(File.ReadAllText(StorePath), JsonOptions);
            if (saved is null) return;
            foreach (var item in saved) _conversations.Add(item);
        }
        catch (Exception ex) { ConnectionLabel.Text = $"History could not be loaded: {ex.Message}"; }
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            await File.WriteAllTextAsync(StorePath, JsonSerializer.Serialize(_conversations, JsonOptions));
        }
        catch (Exception ex) { ConnectionLabel.Text = $"Could not save history: {ex.Message}"; }
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var response = await Http.GetFromJsonAsync<TagsResponse>("api/tags");
            _models.Clear();
            var installed = response?.Models ?? [];
            AddKnownModel(installed, "devstral-small-2-64k", "Devstral Small 2 · Q4 · 64K",
                "devstral-small-2-64k", "devstral-small-2:q4_k_m",
                "hf.co/bartowski/mistralai_Devstral-Small-2-24B-Instruct-2512-GGUF:Q4_K_M");
            AddKnownModel(installed, "qwen3-coder-next-q2-24k", "Qwen3-Coder-Next · Q2 · 24K",
                "qwen3-coder-next-q2-24k", "qwen3-coder-next:q2_k_l",
                "hf.co/bartowski/Qwen_Qwen3-Coder-Next-GGUF:Q2_K_L");
            AddKnownModel(installed, "qwen3-coder:30b", "Qwen3-Coder 30B · Q4 · 64K", "qwen3-coder:30b");

            _loadingModel = true;
            ModelPicker.ItemsSource = _models;
            if (_models.Count > 0)
            {
                var wanted = _active?.Model ?? "devstral-small-2-64k";
                ModelPicker.SelectedValue = FindModelOption(wanted)?.Name ?? _models[0].Name;
            }
            _loadingModel = false;
            ConnectionLabel.Text = _models.Count == 0 ? "No supported local models found" : $"Ollama · {_models.Count} coding models";
        }
        catch
        {
            _loadingModel = true;
            _models.Clear();
            ModelPicker.ItemsSource = _models;
            _loadingModel = false;
            ConnectionLabel.Text = "Ollama is not reachable";
        }
    }

    private void AddKnownModel(List<TagModel> installed, string preferredName, string displayName, params string[] aliases)
    {
        var names = aliases.Append(preferredName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = installed.FirstOrDefault(m => names.Contains(m.Name) || names.Contains(RemoveLatestTag(m.Name)));
        if (actual is not null) _models.Add(new ModelOption(actual.Name, displayName));
    }

    private ModelOption? FindModelOption(string modelName)
    {
        var normalized = RemoveLatestTag(modelName);
        return _models.FirstOrDefault(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase) || RemoveLatestTag(m.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase)) ??
            (IsSameModel(normalized, "devstral-small-2-64k", "devstral-small-2:q4_k_m", "hf.co/bartowski/mistralai_Devstral-Small-2-24B-Instruct-2512-GGUF:Q4_K_M") ? _models.FirstOrDefault(m => m.DisplayName.StartsWith("Devstral Small 2", StringComparison.Ordinal)) : null) ??
            (IsSameModel(normalized, "qwen3-coder-next-q2-24k", "qwen3-coder-next:q2_k_l", "hf.co/bartowski/Qwen_Qwen3-Coder-Next-GGUF:Q2_K_L") ? _models.FirstOrDefault(m => m.DisplayName.StartsWith("Qwen3-Coder-Next", StringComparison.Ordinal)) : null) ??
            (normalized.Equals("qwen3-coder:30b", StringComparison.OrdinalIgnoreCase) ? _models.FirstOrDefault(m => m.DisplayName.StartsWith("Qwen3-Coder 30B", StringComparison.Ordinal)) : null);
    }

    private static bool IsSameModel(string name, params string[] aliases) => aliases.Any(a => RemoveLatestTag(a).Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string RemoveLatestTag(string name) => name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        var model = ModelPicker.SelectedValue as string ?? "devstral-small-2-64k";
        var conversation = new Conversation { Model = model, UpdatedAt = DateTimeOffset.Now };
        _conversations.Insert(0, conversation);
        SelectConversation(conversation);
        RefreshConversationLists();
        PromptBox.Focus();
        _ = SaveAsync();
    }

    private void SelectConversation(Conversation conversation)
    {
        _active = conversation;
        ConversationTitle.Text = string.IsNullOrWhiteSpace(conversation.Title) ? "New conversation" : conversation.Title;
        PinButton.Content = conversation.IsPinned ? "★  Pinned" : "☆  Pin";
        _loadingModel = true;
        ModelPicker.SelectedValue = FindModelOption(conversation.Model)?.Name;
        if (ModelPicker.SelectedValue is null && _models.Count > 0) ModelPicker.SelectedIndex = 0;
        _loadingModel = false;
        WelcomePanel.Visibility = conversation.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderMessages();
        RefreshConversationLists();
        _projectPath = conversation.ProjectPath;
        ContextLabel.Text = _projectPath is null ? "No project attached" : Path.GetFileName(_projectPath);
    }

    private void RenderMessages()
    {
        MessagesList.Items.Clear();
        if (_active is null) return;
        foreach (var message in _active.Messages)
        {
            var isUser = message.Role == "user";
            var body = new TextBlock { Text = message.Content, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23, Foreground = new SolidColorBrush(Color.FromRgb(42, 43, 39)) };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = isUser ? "YOU" : "CODEV", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(isUser ? Color.FromRgb(154, 106, 85) : Color.FromRgb(125, 128, 119)), Margin = new Thickness(0, 0, 0, 6) });
            content.Children.Add(body);
            var border = new Border { Child = content, Padding = new Thickness(isUser ? 15 : 0, isUser ? 12 : 8, isUser ? 15 : 0, isUser ? 12 : 8), Background = isUser ? new SolidColorBrush(Color.FromRgb(239, 237, 231)) : Brushes.Transparent, CornerRadius = new CornerRadius(12), HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, MaxWidth = 720, Margin = new Thickness(0, 0, 0, 17) };
            MessagesList.Items.Add(border);
        }
        ChatScroll.ScrollToEnd();
    }

    private void RefreshConversationLists()
    {
        FillConversationList(PinnedList, _conversations.Where(c => c.IsPinned).OrderByDescending(c => c.UpdatedAt));
        var search = SearchBox.Text?.Trim();
        var recent = _conversations.Where(c => !c.IsPinned).OrderByDescending(c => c.UpdatedAt);
        if (!string.IsNullOrWhiteSpace(search)) recent = recent.Where(c => c.Title.Contains(search, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.UpdatedAt);
        FillConversationList(RecentList, recent);
    }

    private void FillConversationList(ItemsControl list, IEnumerable<Conversation> conversations)
    {
        list.Items.Clear();
        foreach (var item in conversations)
        {
            var title = string.IsNullOrWhiteSpace(item.Title) ? "New conversation" : item.Title;
            var button = new Button { Style = (Style)FindResource("SidebarButton"), Tag = item, Padding = new Thickness(11, 8, 7, 8), Margin = new Thickness(0, 1, 0, 1), Background = ReferenceEquals(item, _active) ? new SolidColorBrush(Color.FromRgb(47, 50, 45)) : Brushes.Transparent };
            var row = new DockPanel();
            var caption = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 188, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(218, 219, 213)), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(caption);
            button.Content = row;
            button.Click += (_, _) => SelectConversation(item);
            var menu = new ContextMenu();
            var pin = new MenuItem { Header = item.IsPinned ? "Unpin conversation" : "Pin conversation" };
            pin.Click += (_, _) => { item.IsPinned = !item.IsPinned; RefreshConversationLists(); _ = SaveAsync(); };
            menu.Items.Add(pin);
            button.ContextMenu = menu;
            list.Items.Add(button);
        }
        if (list.Items.Count == 0) list.Items.Add(new TextBlock { Text = "Nothing here yet", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(113, 117, 108)), Margin = new Thickness(12, 3, 0, 3) });
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private async Task SendPromptAsync()
    {
        var text = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || _active is null || SendButton.IsEnabled == false) return;
        if (ModelPicker.SelectedValue is string model) _active.Model = model;
        var conversation = _active;
        var userMessage = text;
        if (conversation.Messages.Count == 0)
        {
            conversation.Title = MakeTitle(userMessage);
            ConversationTitle.Text = conversation.Title;
        }
        conversation.Messages.Add(new ChatMessage("user", userMessage));
        conversation.Messages.Add(new ChatMessage("assistant", ""));
        conversation.UpdatedAt = DateTimeOffset.Now;
        PromptBox.Clear();
        WelcomePanel.Visibility = Visibility.Collapsed;
        RenderMessages();
        RefreshConversationLists();
        await SaveAsync();
        SendButton.IsEnabled = false;
        SendButton.Content = "…";
        _requestCancellation = new CancellationTokenSource();

        try
        {
            var history = conversation.Messages.Take(conversation.Messages.Count - 1).Select(m => new OllamaMessage(m.Role, m.Content)).ToList();
            var system = "You are Codev, a practical coding assistant. Be concise, explain decisions plainly, and focus on useful implementation details. The user is chatting through a local desktop app. Do not claim you changed files or ran commands; this chat-only preview has no file editing or command execution yet.";
            if (!string.IsNullOrWhiteSpace(conversation.ProjectPath))
            {
                system += "\n\nThe user attached this local project folder: " + conversation.ProjectPath + ". Here are selected source files from that folder, included as read-only context. Do not claim to have changed them.";
                system += "\n\n" + await CollectProjectContextAsync(conversation.ProjectPath, _requestCancellation.Token);
            }
            history.Insert(0, new OllamaMessage("system", system));
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(new { model = conversation.Model, messages = history, stream = true }) };
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _requestCancellation.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(_requestCancellation.Token);
            using var reader = new StreamReader(stream);
            var output = new StringBuilder();
            while (await reader.ReadLineAsync(_requestCancellation.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
                if (json.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var chunk))
                {
                    output.Append(chunk.GetString());
                    conversation.Messages[^1] = new ChatMessage("assistant", output.ToString());
                    if (ReferenceEquals(_active, conversation)) RenderMessages();
                }
            }
            if (output.Length == 0) conversation.Messages[^1] = new ChatMessage("assistant", "The model returned an empty response. Check that the selected model is installed and running in Ollama.");
        }
        catch (OperationCanceledException) { conversation.Messages[^1] = new ChatMessage("assistant", "Generation stopped."); }
        catch (Exception ex) { conversation.Messages[^1] = new ChatMessage("assistant", $"Could not reach the selected Ollama model.\n\n{ex.Message}\n\nCheck that Ollama is running and that this model is installed."); }
        finally
        {
            conversation.UpdatedAt = DateTimeOffset.Now;
            if (ReferenceEquals(_active, conversation)) RenderMessages();
            RefreshConversationLists();
            await SaveAsync();
            SendButton.IsEnabled = true;
            SendButton.Content = "↑";
            _requestCancellation?.Dispose();
            _requestCancellation = null;
        }
    }

    private static string MakeTitle(string prompt)
    {
        var oneLine = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length > 36 ? oneLine[..33] + "…" : oneLine;
    }

    private static async Task<string> CollectProjectContextAsync(string root, CancellationToken cancellationToken)
    {
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".csproj", ".sln", ".md", ".json", ".js", ".jsx", ".ts", ".tsx", ".py", ".html", ".css", ".sql", ".xml", ".yml", ".yaml", ".toml", ".props", ".targets" };
        var ignoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "coverage" };
        var output = new StringBuilder("Selected project files (limited read-only excerpts):\n");
        var count = 0;
        const int maxFiles = 24;
        const int maxChars = 32000;
        const int maxFileChars = 2400;
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && count < maxFiles && output.Length < maxChars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                IEnumerable<string> children;
                try { children = Directory.EnumerateFileSystemEntries(dir); }
                catch { continue; }
                foreach (var path in children)
                {
                    if (Directory.Exists(path))
                    {
                        if (!ignoredDirectories.Contains(Path.GetFileName(path))) pending.Push(path);
                        continue;
                    }
                    if (!allowedExtensions.Contains(Path.GetExtension(path))) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length > 500_000) continue;
                        var content = await File.ReadAllTextAsync(path, cancellationToken);
                        if (content.Length > maxFileChars) content = content[..maxFileChars] + "\n… [excerpt truncated]";
                        var relative = Path.GetRelativePath(root, path);
                        output.Append("\n--- ").Append(relative).AppendLine(" ---\n").AppendLine(content);
                        count++;
                        if (count >= maxFiles || output.Length >= maxChars) break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            if (count == 0) output.Append("No supported text source files were found. Ask the user to paste relevant code if needed.");
            else output.Append("\n[Context is limited to ").Append(count).AppendLine(" source files. Ask for specific files if you need more detail.]");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { output.Append("\nCould not read project context: ").Append(ex.Message); }
        return output.ToString();
    }

    private void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; _ = SendPromptAsync(); }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; PromptBox.AppendText(Environment.NewLine); }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _active.IsPinned = !_active.IsPinned;
        PinButton.Content = _active.IsPinned ? "★  Pinned" : "☆  Pin";
        RefreshConversationLists();
        _ = SaveAsync();
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingModel || _active is null || ModelPicker.SelectedValue is not string model) return;
        _active.Model = model;
        _ = SaveAsync();
    }

    private void SearchFocus_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Visibility = SearchBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SearchBox.Visibility == Visibility.Visible) SearchBox.Focus();
        else { SearchBox.Clear(); RefreshConversationLists(); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshConversationLists();

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text }) { PromptBox.Text = text; PromptBox.CaretIndex = PromptBox.Text.Length; PromptBox.Focus(); }
    }

    private void AddContext_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a project folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _projectPath = dialog.FolderName;
        if (_active is not null) { _active.ProjectPath = _projectPath; _ = SaveAsync(); }
        ContextLabel.Text = Path.GetFileName(_projectPath);
        ContextLabel.ToolTip = _projectPath;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Codev connects to Ollama at http://127.0.0.1:11434. Conversations are stored on this device in %LOCALAPPDATA%\\Codev.\n\nThis first preview provides local chat, model switching, search, and pinned conversations. Project-aware file reading and safe edit approvals are planned next.", "Codev settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnClosed(EventArgs e)
    {
        _requestCancellation?.Cancel();
        _ = SaveAsync();
        base.OnClosed(e);
    }

    private sealed record OllamaMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);
    private sealed class TagsResponse { [JsonPropertyName("models")] public List<TagModel>? Models { get; set; } }
    private sealed class TagModel { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    private sealed record ModelOption(string Name, string DisplayName);
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Model { get; set; } = "devstral-small-2-64k";
    public bool IsPinned { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? ProjectPath { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed record ChatMessage(string Role, string Content);

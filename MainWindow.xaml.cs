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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Codev;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ObservableCollection<Conversation> _conversations = [];
    private readonly ObservableCollection<WorkspaceProject> _projects = [];
    private readonly List<ModelOption> _models = [];
    private readonly List<ContextOption> _contextSizes = [new(0, "Model default"), new(8192, "8K"), new(16384, "16K"), new(24576, "24K"), new(32768, "32K"), new(49152, "48K"), new(65536, "64K"), new(98304, "96K")];
    private Conversation? _active;
    private WorkspaceProject? _activeProject;
    private readonly SerialAsyncQueue<QueuedTurn> _requestQueue = new();
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private string? _projectPath;
    private CancellationTokenSource? _requestCancellation;
    private Conversation? _activeRequestConversation;
    private bool _isClosing;
    private bool _queuePaused;
    private bool _activeRequestIsCodeTask;
    private bool _loadingModel;
    private bool _updatingContext;
    private bool _codeTaskMode;
    private bool _planMode;
    private bool _showArchived;
    private Guid? _codeTaskConversationId;
    private bool _isDarkTheme = true;
    private double _chatFontSize = 14;

    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "conversations.json");
    private static string ThemePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "settings.json");
    private static string ProjectsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "projects.json");

    public MainWindow()
    {
        InitializeComponent();
        var contextMenu = new ContextMenu();
        var clearContext = new MenuItem { Header = "Clear selected context files" };
        clearContext.Click += async (_, _) =>
        {
            if (_active is null) return;
            _active.ContextFiles.Clear();
            UpdateContextLabel(_active);
            await SaveAsync();
        };
        contextMenu.Items.Add(clearContext);
        AddContextButton.ContextMenu = contextMenu;
        LoadThemePreference();
        ApplyTheme();
        ApplyChatTextSize();
        LoadProjects();
        LoadConversations();
        foreach (var path in _conversations.Select(c => c.ProjectPath).Where(p => !string.IsNullOrWhiteSpace(p))) EnsureProject(path!);
        RefreshConversationLists();
        if (_conversations.Count > 0) SelectConversation(_conversations.OrderByDescending(c => c.UpdatedAt).First());
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += async (_, _) => await LoadModelsAsync();
    }

    private void LoadThemePreference()
    {
        try
        {
            if (File.Exists(ThemePath))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(ThemePath), JsonOptions);
                _isDarkTheme = !string.Equals(settings?.Theme, "light", StringComparison.OrdinalIgnoreCase);
                _chatFontSize = settings?.ChatFontSize is double storedSize && double.IsFinite(storedSize) ? Math.Clamp(storedSize, 12, 22) : 14;
            }
        }
        catch { _isDarkTheme = true; }
    }

    private void SaveThemePreference()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)!);
            File.WriteAllText(ThemePath, JsonSerializer.Serialize(new UiSettings(_isDarkTheme ? "dark" : "light", _chatFontSize), JsonOptions));
        }
        catch { }
    }

    private void ApplyTheme()
    {
        var palette = _isDarkTheme
            ? new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#242522", ["SidebarBackgroundBrush"] = "#1B1C1A", ["SidebarTextBrush"] = "#D7D8D2",
                ["SidebarMutedBrush"] = "#888B83", ["SidebarBorderBrush"] = "#383A35", ["SidebarHoverBrush"] = "#292C28",
                ["SidebarActiveBrush"] = "#30332F", ["SidebarCardBrush"] = "#272A26", ["SidebarInputBrush"] = "#292C28",
                ["SidebarNewButtonBrush"] = "#30332F", ["SidebarNewButtonTextBrush"] = "#FFFFFF", ["MainSurfaceBrush"] = "#292A27",
                ["MainSurfaceAltBrush"] = "#242522", ["MainBorderBrush"] = "#3A3B37", ["MainTextBrush"] = "#ECECE6",
                ["MutedTextBrush"] = "#A2A49C", ["ComposerBrush"] = "#2B2C29", ["ComposerBorderBrush"] = "#41423D",
                ["SecondaryButtonBrush"] = "#383A35", ["SecondaryButtonHoverBrush"] = "#444640", ["SecondaryButtonTextBrush"] = "#E0E1DB",
                ["InputTextBrush"] = "#ECECE6", ["MessageBubbleBrush"] = "#343530", ["MessageTextBrush"] = "#E4E5DF",
                ["UserLabelBrush"] = "#D29A7F", ["AssistantLabelBrush"] = "#A2A49C", ["WelcomeAccentBackgroundBrush"] = "#44352F",
                ["WelcomeAccentBrush"] = "#E18A6A", ["ComboPopupBrush"] = "#2B2C29", ["ComboHoverBrush"] = "#383A35",
                ["ComboSelectedBrush"] = "#48443F", ["ComboSelectedTextBrush"] = "#F4F1EA",
                ["SyntaxKeywordBrush"] = "#C792EA", ["SyntaxTypeBrush"] = "#82AAFF", ["SyntaxStringBrush"] = "#C3E88D",
                ["SyntaxNumberBrush"] = "#F78C6C", ["SyntaxCommentBrush"] = "#7F8C7D"
            }
            : new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#F7F7F4", ["SidebarBackgroundBrush"] = "#F0F0EC", ["SidebarTextBrush"] = "#353731",
                ["SidebarMutedBrush"] = "#85877F", ["SidebarBorderBrush"] = "#D9DAD4", ["SidebarHoverBrush"] = "#E6E7E1",
                ["SidebarActiveBrush"] = "#E1E2DC", ["SidebarCardBrush"] = "#E6E7E1", ["SidebarInputBrush"] = "#E5E6E0",
                ["SidebarNewButtonBrush"] = "#D97757", ["SidebarNewButtonTextBrush"] = "#FFFFFF", ["MainSurfaceBrush"] = "#FFFFFF",
                ["MainSurfaceAltBrush"] = "#F9F9F6", ["MainBorderBrush"] = "#E6E7E1", ["MainTextBrush"] = "#282A26",
                ["MutedTextBrush"] = "#85877F", ["ComposerBrush"] = "#FFFFFF", ["ComposerBorderBrush"] = "#E2E3DD",
                ["SecondaryButtonBrush"] = "#EEEFEA", ["SecondaryButtonHoverBrush"] = "#E4E5DF", ["SecondaryButtonTextBrush"] = "#41433E",
                ["InputTextBrush"] = "#272925", ["MessageBubbleBrush"] = "#EFEEE8", ["MessageTextBrush"] = "#2A2B27",
                ["UserLabelBrush"] = "#9A6A55", ["AssistantLabelBrush"] = "#7D8077", ["WelcomeAccentBackgroundBrush"] = "#F3E3DC",
                ["WelcomeAccentBrush"] = "#C66F53", ["ComboPopupBrush"] = "#FFFFFF", ["ComboHoverBrush"] = "#F0F1EC",
                ["ComboSelectedBrush"] = "#F4E1D8", ["ComboSelectedTextBrush"] = "#45352F",
                ["SyntaxKeywordBrush"] = "#7C3AED", ["SyntaxTypeBrush"] = "#1D4ED8", ["SyntaxStringBrush"] = "#287A37",
                ["SyntaxNumberBrush"] = "#B45309", ["SyntaxCommentBrush"] = "#788178"
            };

        foreach (var (key, value) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            Resources[key] = brush;
        }
        ThemeButton.Content = _isDarkTheme ? "☼  Switch to light mode" : "☾  Switch to dark mode";
    }

    private void LoadConversations()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var saved = JsonSerializer.Deserialize<List<Conversation>>(File.ReadAllText(StorePath), JsonOptions);
            if (saved is null) return;
            foreach (var item in saved)
            {
                for (var index = 0; index < item.Messages.Count; index++)
                    if (item.Messages[index].Role == "assistant" && string.IsNullOrWhiteSpace(item.Messages[index].Content))
                        item.Messages[index] = new ChatMessage("assistant", "This request did not finish before Codev closed.");
                _conversations.Add(item);
            }
        }
        catch (Exception ex) { ConnectionLabel.Text = $"History could not be loaded: {ex.Message}"; }
    }

    private void LoadProjects()
    {
        try
        {
            if (!File.Exists(ProjectsPath)) return;
            var saved = JsonSerializer.Deserialize<List<WorkspaceProject>>(File.ReadAllText(ProjectsPath), JsonOptions);
            if (saved is null) return;
            foreach (var project in saved.Where(p => !string.IsNullOrWhiteSpace(p.Path)).OrderByDescending(p => p.LastOpenedAt)) _projects.Add(project);
        }
        catch (Exception ex) { ConnectionLabel.Text = $"Project list could not be loaded: {ex.Message}"; }
    }

    private async Task SaveProjectsAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectsPath)!);
            await File.WriteAllTextAsync(ProjectsPath, JsonSerializer.Serialize(_projects, JsonOptions));
        }
        catch (Exception ex) { ConnectionLabel.Text = $"Projects could not be saved: {ex.Message}"; }
    }

    private WorkspaceProject EnsureProject(string path)
    {
        var normalized = Path.GetFullPath(path);
        var existing = _projects.FirstOrDefault(p => SamePath(p.Path, normalized));
        if (existing is not null) return existing;
        var project = new WorkspaceProject { Name = new DirectoryInfo(normalized).Name, Path = normalized, LastOpenedAt = DateTimeOffset.Now };
        _projects.Add(project);
        _ = SaveProjectsAsync();
        return project;
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private async Task SaveAsync()
    {
        var gateHeld = false;
        try
        {
            await _storeGate.WaitAsync();
            gateHeld = true;
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            await File.WriteAllTextAsync(StorePath, JsonSerializer.Serialize(_conversations, JsonOptions));
        }
        catch (Exception ex) { ConnectionLabel.Text = $"Could not save history: {ex.Message}"; }
        finally { if (gateHeld) _storeGate.Release(); }
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
            RefreshContextPicker(_active);
            ConnectionLabel.Text = _models.Count == 0 ? "No supported local models found" : $"Ollama · {_models.Count} coding models";
            RefreshConversationLists();
        }
        catch
        {
            _loadingModel = true;
            _models.Clear();
            ModelPicker.ItemsSource = _models;
            _loadingModel = false;
            RefreshContextPicker(_active);
            ConnectionLabel.Text = "Ollama is not reachable";
            RefreshConversationLists();
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
        var projectPath = _activeProject?.Path ?? _active?.ProjectPath;
        var conversation = new Conversation { Model = model, ProjectPath = projectPath, UpdatedAt = DateTimeOffset.Now };
        _conversations.Insert(0, conversation);
        SelectConversation(conversation);
        RefreshConversationLists();
        PromptBox.Focus();
        _ = SaveAsync();
    }

    private void SelectConversation(Conversation conversation)
    {
        _active = conversation;
        ExportConversationButton.IsEnabled = true;
        _codeTaskMode = false;
        _planMode = false;
        _codeTaskConversationId = null;
        UpdateModeButtons();
        _activeProject = conversation.ProjectPath is null ? null : EnsureProject(conversation.ProjectPath);
        UpdateChangesButton(conversation);
        UpdateSendControl();
        UpdateActiveRequestStatus();
        if (_activeProject is not null) _activeProject.LastOpenedAt = DateTimeOffset.Now;
        ConversationTitle.Text = string.IsNullOrWhiteSpace(conversation.Title) ? "New conversation" : conversation.Title;
        PinButton.Content = conversation.IsPinned ? "★  Pinned" : "☆  Pin";
        _loadingModel = true;
        ModelPicker.SelectedValue = FindModelOption(conversation.Model)?.Name;
        if (ModelPicker.SelectedValue is null && _models.Count > 0) ModelPicker.SelectedIndex = 0;
        _loadingModel = false;
        RefreshContextPicker(conversation);
        WelcomePanel.Visibility = conversation.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderMessages();
        RefreshConversationLists();
        _projectPath = conversation.ProjectPath;
        UpdateContextLabel(conversation);
    }

    private void RenderMessages()
    {
        MessagesList.Items.Clear();
        if (_active is null) return;
        var conversation = _active;
        for (var messageIndex = 0; messageIndex < conversation.Messages.Count; messageIndex++)
        {
            var message = conversation.Messages[messageIndex];
            var isUser = message.Role == "user";
            FrameworkElement body = isUser
                ? new TextBlock { Text = message.Content, TextWrapping = TextWrapping.Wrap, FontSize = _chatFontSize, LineHeight = _chatFontSize * 1.6, Foreground = ThemeBrush("MessageTextBrush") }
                : MarkdownRenderer.Render(message.Content, ThemeBrush("MessageTextBrush"), ThemeBrush("MutedTextBrush"), ThemeBrush("MessageBubbleBrush"), ThemeBrush("WelcomeAccentBrush"), _chatFontSize,
                    ThemeBrush("SyntaxKeywordBrush"), ThemeBrush("SyntaxTypeBrush"), ThemeBrush("SyntaxStringBrush"), ThemeBrush("SyntaxNumberBrush"), ThemeBrush("SyntaxCommentBrush"));
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = isUser ? "YOU" : "CODEV", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush(isUser ? "UserLabelBrush" : "AssistantLabelBrush"), Margin = new Thickness(0, 0, 0, 6) });
            content.Children.Add(body);
            var border = new Border { Child = content, Padding = new Thickness(isUser ? 15 : 0, isUser ? 12 : 8, isUser ? 15 : 0, isUser ? 12 : 8), Background = isUser ? ThemeBrush("MessageBubbleBrush") : Brushes.Transparent, CornerRadius = new CornerRadius(12), HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, MaxWidth = 720, Margin = new Thickness(0, 0, 0, 17) };
            if (_requestCancellation is null && conversation.PendingRequestCount == 0)
            {
                var menu = new ContextMenu();
                var branchIndex = messageIndex;
                var branch = new MenuItem { Header = "Branch conversation from here" };
                branch.Click += async (_, _) => await BranchConversationAsync(conversation, branchIndex);
                menu.Items.Add(branch);
                if (isUser && messageIndex == conversation.Messages.Count - 2 && conversation.Messages[^1].Role == "assistant")
                {
                    var edit = new MenuItem { Header = "Edit & resend" };
                    edit.Click += async (_, _) => await ResendFromUserMessageAsync(conversation, message);
                    menu.Items.Add(edit);
                }
                else if (!isUser && messageIndex == conversation.Messages.Count - 1 && conversation.Messages.Count >= 2 && conversation.Messages[^2].Role == "user")
                {
                    var retry = new MenuItem { Header = "Regenerate response" };
                    retry.Click += async (_, _) => await ResendFromUserMessageAsync(conversation, conversation.Messages[^2]);
                    menu.Items.Add(retry);
                }
                if (menu.Items.Count > 0) border.ContextMenu = menu;
            }
            MessagesList.Items.Add(border);
        }
        ChatScroll.ScrollToEnd();
    }

    private async Task ResendFromUserMessageAsync(Conversation conversation, ChatMessage userMessage)
    {
        if (!ReferenceEquals(_active, conversation) || conversation.PendingRequestCount > 0 || ReferenceEquals(_activeRequestConversation, conversation)) return;
        var index = conversation.Messages.FindIndex(message => ReferenceEquals(message, userMessage));
        if (index < 0) return;
        PromptBox.Text = userMessage.Content;
        PromptBox.CaretIndex = PromptBox.Text.Length;
        conversation.Messages.RemoveRange(index, conversation.Messages.Count - index);
        RenderMessages();
        _ = SaveAsync();
        PromptBox.Focus();
        await SendPromptAsync();
    }

    private async Task BranchConversationAsync(Conversation source, int messageIndex)
    {
        if (!ReferenceEquals(_active, source) || source.PendingRequestCount > 0 || ReferenceEquals(_activeRequestConversation, source) || messageIndex < 0 || messageIndex >= source.Messages.Count) return;
        var title = string.IsNullOrWhiteSpace(source.Title) ? "Conversation branch" : source.Title + " · branch";
        var branch = new Conversation
        {
            Title = title,
            Model = source.Model,
            NumCtx = source.NumCtx,
            ProjectPath = source.ProjectPath,
            ContextFiles = [.. source.ContextFiles],
            UpdatedAt = DateTimeOffset.Now,
            Messages = source.Messages.Take(messageIndex + 1).Select(m => new ChatMessage(m.Role, m.Content)).ToList()
        };
        _conversations.Insert(0, branch);
        SelectConversation(branch);
        RefreshConversationLists();
        PromptBox.Focus();
        await SaveAsync();
    }

    private void RefreshConversationLists()
    {
        FillProjectsList();
        var inWorkspace = _conversations.Where(c => SameWorkspace(c.ProjectPath, _activeProject?.Path) && c.IsArchived == _showArchived);
        FillConversationList(PinnedList, inWorkspace.Where(c => c.IsPinned).OrderByDescending(c => c.UpdatedAt));
        var search = SearchBox.Text?.Trim();
        var recent = inWorkspace.Where(c => !c.IsPinned).OrderByDescending(c => c.UpdatedAt);
        if (!string.IsNullOrWhiteSpace(search)) recent = recent.Where(c => c.Title.Contains(search, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.UpdatedAt);
        FillConversationList(RecentList, recent);
    }

    private static string FormatRelativeTime(DateTimeOffset updatedAt)
    {
        var age = DateTimeOffset.Now - updatedAt;
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays}d ago";
        return updatedAt.LocalDateTime.ToString("MMM d");
    }

    private static bool SameWorkspace(string? conversationPath, string? workspacePath) =>
        conversationPath is null ? workspacePath is null : workspacePath is not null && SamePath(conversationPath, workspacePath);

    private void FillProjectsList()
    {
        ProjectsList.Items.Clear();
        AddProjectButton("All chats", null, _activeProject is null);
        foreach (var project in _projects.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.LastOpenedAt))
            AddProjectButton(project.Name, project, ReferenceEquals(project, _activeProject));
    }

    private void AddProjectButton(string title, WorkspaceProject? project, bool isSelected)
    {
        var prefix = project?.IsPinned == true ? "★  " : "";
        var button = new Button
        {
            Style = (Style)FindResource("SidebarButton"), Tag = project,
            Padding = new Thickness(11, 7, 7, 7), Margin = new Thickness(0, 1, 0, 1),
            Background = isSelected ? ThemeBrush("SidebarActiveBrush") : Brushes.Transparent,
            ToolTip = project?.Path ?? "Conversations without a project"
        };
        var row = new DockPanel();
        row.Children.Add(new TextBlock
        {
            Text = prefix + title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 188,
            FontSize = 12, Foreground = ThemeBrush("SidebarTextBrush"), VerticalAlignment = VerticalAlignment.Center
        });
        button.Content = row;
        button.Click += (_, _) => OpenWorkspace(project);
        if (project is not null)
        {
            var menu = new ContextMenu();
            var pinItem = new MenuItem { Header = project.IsPinned ? "Unpin project" : "Pin project" };
            pinItem.Click += async (_, _) => { project.IsPinned = !project.IsPinned; RefreshConversationLists(); await SaveProjectsAsync(); };
            var openItem = new MenuItem { Header = "Open folder in File Explorer" };
            openItem.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = project.Path, UseShellExecute = true });
            var instructionsItem = new MenuItem { Header = "Edit project instructions…" };
            instructionsItem.Click += async (_, _) => await EditProjectInstructionsAsync(project);
            var exclusionsItem = new MenuItem { Header = "Context exclusions…" };
            exclusionsItem.Click += async (_, _) => await EditProjectContextExclusionsAsync(project);
            var searchContentsItem = new MenuItem { Header = "Search project contents…" };
            searchContentsItem.Click += (_, _) => SearchProjectContents(project);
            var gitStatusItem = new MenuItem { Header = "Git status & branches…" };
            gitStatusItem.Click += async (_, _) => await ShowGitStatusAsync(project);
            var browseItem = new MenuItem { Header = "Browse project files…" };
            browseItem.Click += (_, _) => BrowseProjectFiles(project);
            menu.Items.Add(pinItem);
            menu.Items.Add(instructionsItem);
            menu.Items.Add(exclusionsItem);
            menu.Items.Add(gitStatusItem);
            menu.Items.Add(searchContentsItem);
            menu.Items.Add(browseItem);
            menu.Items.Add(openItem);
            button.ContextMenu = menu;
        }
        ProjectsList.Items.Add(button);
    }

    private async Task ShowGitStatusAsync(WorkspaceProject project)
    {
        GitRepositoryService service;
        GitRepositoryStatus status;
        IReadOnlyList<string> branches;
        try
        {
            service = new GitRepositoryService(project.Path);
            status = await service.GetStatusAsync();
            branches = await service.GetLocalBranchesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Git status is unavailable.\n\n{ex.Message}", "Git status", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = "Git status", Width = 720, Height = 520, MinWidth = 560, MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"), ResizeMode = ResizeMode.CanResize
        };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel();
        var rootLabel = new TextBlock { Text = status.Root, FontSize = 11, Foreground = ThemeBrush("MutedTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = status.Root };
        header.Children.Add(rootLabel);
        var branchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 10) };
        branchRow.Children.Add(new TextBlock { Text = "Branch", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var branchPicker = new ComboBox { Width = 210, ItemsSource = branches, SelectedItem = status.Branch, IsEnabled = branches.Count > 0 };
        branchRow.Children.Add(branchPicker);
        var switchButton = new Button { Content = "Switch…", Style = (Style)FindResource("SoftButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(11, 6, 11, 6) };
        branchRow.Children.Add(switchButton);
        var refreshButton = new Button { Content = "Refresh", Style = (Style)FindResource("SoftButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(11, 6, 11, 6) };
        branchRow.Children.Add(refreshButton);
        header.Children.Add(branchRow);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = ThemeBrush("MutedTextBrush") };
        header.Children.Add(summary);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        var stageButton = new Button { Content = "Stage selected", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(10, 6, 10, 6), IsEnabled = false };
        actions.Children.Add(stageButton);
        var commitButton = new Button { Content = "Review staged diff & commit…", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(7, 0, 0, 0), IsEnabled = false };
        actions.Children.Add(commitButton);
        left.Children.Add(actions);
        var files = new ListBox { Background = ThemeBrush("MainSurfaceAltBrush"), BorderBrush = ThemeBrush("MainBorderBrush"), Foreground = ThemeBrush("MainTextBrush") };
        left.Children.Add(files);
        var diffVersion = 0;
        Grid.SetColumn(left, 0);
        body.Children.Add(left);
        var diffBox = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 11, Background = ThemeBrush("MainSurfaceAltBrush"),
            Foreground = ThemeBrush("MainTextBrush"), BorderBrush = ThemeBrush("MainBorderBrush"), Padding = new Thickness(10)
        };
        Grid.SetColumn(diffBox, 1);
        body.Children.Add(diffBox);
        layout.Children.Add(body);
        dialog.Content = layout;

        void RenderStatus(GitRepositoryStatus value)
        {
            var tracking = value.Upstream is null ? "no upstream" : value.Upstream + (value.Ahead > 0 || value.Behind > 0 ? $" · ahead {value.Ahead}, behind {value.Behind}" : " · up to date");
            summary.Text = value.HasChanges ? $"{value.Files.Count} changed file(s) · {tracking}" : $"Working tree clean · {tracking}";
            files.Items.Clear();
            stageButton.IsEnabled = false;
            diffBox.Text = value.HasChanges ? "Select a changed file to inspect its staged and unstaged diff." : "The working tree is clean.";
            if (!value.HasChanges)
                files.Items.Add(new ListBoxItem { Content = "No staged, unstaged, or untracked changes.", IsEnabled = false, Padding = new Thickness(8) });
            foreach (var file in value.Files)
                files.Items.Add(new ListBoxItem
                {
                    Content = $"{file.State.Replace(' ', '·')}   {file.DisplayPath}",
                    ToolTip = $"Index: {file.Staged} · Working tree: {file.WorkingTree} · {file.DisplayPath}",
                    FontFamily = new FontFamily("Consolas"), Padding = new Thickness(8), Tag = file
                });
            commitButton.IsEnabled = value.Files.Any(file => file.Staged != " ");
            UpdateSwitchControl();
        }

        void UpdateSwitchControl() => switchButton.IsEnabled = !status.HasChanges && branches.Count > 0 && branchPicker.SelectedItem is string selected && selected != status.Branch;

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
                switchButton.IsEnabled = false;
                stageButton.IsEnabled = false;
                commitButton.IsEnabled = false;
                diffBox.Text = "Git status could not be loaded.";
            }
        }

        files.SelectionChanged += async (_, _) =>
        {
            var version = ++diffVersion;
            if (files.SelectedItem is not ListBoxItem { Tag: GitFileStatus file })
            {
                stageButton.IsEnabled = false;
                return;
            }
            stageButton.Content = file.Staged != " " && file.WorkingTree == " " ? "Unstage selected" : "Stage selected";
            stageButton.IsEnabled = true;
            diffBox.Text = "Loading diff…";
            try
            {
                var diff = await service.GetFileDiffAsync(file);
                if (version != diffVersion) return;
                diffBox.Text = string.IsNullOrWhiteSpace(diff) ? "Git reported no textual diff for this file (it may be binary or unchanged since the index was refreshed)." : diff;
            }
            catch (Exception ex) { if (version == diffVersion) diffBox.Text = "Could not load diff: " + ex.Message; }
        };
        stageButton.Click += async (_, _) =>
        {
            if (files.SelectedItem is not ListBoxItem { Tag: GitFileStatus file }) return;
            try
            {
                if (file.Staged != " " && file.WorkingTree == " ") await service.UnstageFileAsync(file.Path);
                else await service.StageFileAsync(file.Path);
                await RefreshAsync();
            }
            catch (Exception ex) { MessageBox.Show(dialog, $"Could not update the Git index.\n\n{ex.Message}", "Stage change", MessageBoxButton.OK, MessageBoxImage.Error); await RefreshAsync(); }
        };
        commitButton.Click += async (_, _) =>
        {
            var stagedReview = await service.GetStagedReviewAsync();
            var diff = stagedReview.Diff;
            if (string.IsNullOrWhiteSpace(diff))
            {
                MessageBox.Show(dialog, "There are no staged textual changes to review.", "Review staged changes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var commitDialog = new Window
            {
                Title = "Review staged changes", Width = 900, Height = 680, MinWidth = 640, MinHeight = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = dialog,
                Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"), ResizeMode = ResizeMode.CanResize
            };
            var commitLayout = new DockPanel { Margin = new Thickness(16) };
            var commitActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(commitActions, Dock.Bottom);
            var cancelCommit = new Button { Content = "Cancel", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var createCommit = new Button { Content = "Create local commit", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
            commitActions.Children.Add(cancelCommit); commitActions.Children.Add(createCommit);
            commitLayout.Children.Add(commitActions);
            var commitMessage = new TextBox { Height = 34, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(8, 6, 8, 6), ToolTip = "Commit message" };
            DockPanel.SetDock(commitMessage, Dock.Bottom);
            commitLayout.Children.Add(commitMessage);
            var review = new DockPanel();
            var reviewIntro = new TextBlock { Text = "Review the exact staged diff below. Unstaged working-tree edits will not be included in this commit.", TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(reviewIntro, Dock.Top); review.Children.Add(reviewIntro);
            review.Children.Add(new TextBox
            {
                Text = diff, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"), FontSize = 11, Background = ThemeBrush("MainSurfaceAltBrush"),
                Foreground = ThemeBrush("MainTextBrush"), BorderBrush = ThemeBrush("MainBorderBrush"), Padding = new Thickness(10)
            });
            commitLayout.Children.Add(review);
            commitDialog.Content = commitLayout;
            createCommit.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(commitMessage.Text)) { MessageBox.Show(commitDialog, "Enter a commit message.", "Commit message required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                var confirmation = MessageBox.Show(commitDialog, $"Create a local commit with this message?\n\n{commitMessage.Text.Trim()}\n\nThis will not push to GitHub.", "Confirm local commit", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirmation != MessageBoxResult.Yes) return;
                createCommit.IsEnabled = false;
                try
                {
                    await service.CommitAsync(commitMessage.Text, stagedReview);
                    commitDialog.DialogResult = true;
                    commitDialog.Close();
                    await RefreshAsync();
                }
                catch (Exception ex)
                {
                    createCommit.IsEnabled = true;
                    MessageBox.Show(commitDialog, $"Could not create the commit.\n\n{ex.Message}", "Commit failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            commitDialog.ShowDialog();
        };
        branchPicker.SelectionChanged += (_, _) => UpdateSwitchControl();
        refreshButton.Click += async (_, _) => await RefreshAsync();
        switchButton.Click += async (_, _) =>
        {
            if (branchPicker.SelectedItem is not string selected || selected == status.Branch || status.HasChanges) return;
            var answer = MessageBox.Show(dialog, $"Switch from '{status.Branch}' to '{selected}'? Git will update the project files to match that branch.", "Switch Git branch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            try
            {
                await service.SwitchBranchAsync(selected);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, $"Could not switch branches.\n\n{ex.Message}", "Git branch switch failed", MessageBoxButton.OK, MessageBoxImage.Error);
                await RefreshAsync();
            }
        };
        RenderStatus(status);
        dialog.ShowDialog();
    }

    private void OpenWorkspace(WorkspaceProject? project)
    {
        _activeProject = project;
        if (project is not null) project.LastOpenedAt = DateTimeOffset.Now;
        _showArchived = false;
        ArchiveViewButton.Content = "◷  Show archived";
        RefreshConversationLists();
        var latest = _conversations.Where(c => !c.IsArchived && SameWorkspace(c.ProjectPath, project?.Path)).OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
        if (latest is not null) SelectConversation(latest);
        else
        {
            var conversation = new Conversation
            {
                Model = ModelPicker.SelectedValue as string ?? "devstral-small-2-64k",
                ProjectPath = project?.Path,
                UpdatedAt = DateTimeOffset.Now
            };
            _conversations.Insert(0, conversation);
            SelectConversation(conversation);
            _ = SaveAsync();
        }
        _ = SaveProjectsAsync();
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Open a project folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            var project = EnsureProject(dialog.FolderName);
            OpenWorkspace(project);
        }
    }

    private async Task EditProjectInstructionsAsync(WorkspaceProject project)
    {
        var editor = new Window
        {
            Title = $"Instructions · {project.Name}", Width = 560, Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"),
            ResizeMode = ResizeMode.CanResize
        };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var help = new TextBlock
        {
            Text = "These instructions are added to every conversation in this project. Keep them specific to its language, conventions, and test commands.",
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 12)
        };
        layout.Children.Add(help);
        var input = new TextBox
        {
            Text = project.Instructions, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13,
            Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"),
            BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(10), MinHeight = 200
        };
        Grid.SetRow(input, 1); layout.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Save instructions", Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        save.Click += (_, _) => { project.Instructions = input.Text.Trim(); editor.DialogResult = true; editor.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons);
        editor.Content = layout;
        if (editor.ShowDialog() == true) await SaveProjectsAsync();
    }

    private async Task EditProjectContextExclusionsAsync(WorkspaceProject project)
    {
        var editor = new Window
        {
            Title = $"Context exclusions · {project.Name}", Width = 560, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"),
            ResizeMode = ResizeMode.CanResize
        };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var help = new TextBlock
        {
            Text = "One project-relative file or folder per line. Folder names exclude that folder anywhere in the project; paths can target a subtree. Use a filename pattern such as *.min.js for generated files. These rules apply to chat context only; Code task file tools can still inspect excluded files.",
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 12)
        };
        layout.Children.Add(help);
        var input = new TextBox
        {
            Text = string.Join(Environment.NewLine, project.ContextExclusions), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13,
            Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"),
            BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(10), MinHeight = 220, FontFamily = new FontFamily("Consolas")
        };
        Grid.SetRow(input, 1); layout.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Save exclusions", Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        save.Click += (_, _) =>
        {
            var rules = input.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (rules.Count > 100 || rules.Any(rule => !WorkspaceFileService.IsValidContextExclusion(rule)))
            {
                MessageBox.Show(editor, "Use up to 100 relative file/folder paths or filename patterns. Absolute paths and .. segments are not allowed.", "Invalid exclusions", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            project.ContextExclusions = rules;
            editor.DialogResult = true;
            editor.Close();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons);
        editor.Content = layout;
        if (editor.ShowDialog() == true)
        {
            await SaveProjectsAsync();
            foreach (var conversation in _conversations.Where(c => c.ProjectPath is not null && SamePath(c.ProjectPath, project.Path)))
                if (ReferenceEquals(_active, conversation)) UpdateContextLabel(conversation);
        }
    }

    private void BrowseProjectFiles(WorkspaceProject project)
    {
        try
        {
            var service = new WorkspaceFileService(project.Path);
            var files = service.ListFiles(maxEntries: 500).ToArray();
            var dialog = new Window
            {
                Title = $"Files · {project.Name}", Width = 980, Height = 660,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"),
                ResizeMode = ResizeMode.CanResize
            };
            var layout = new Grid { Margin = new Thickness(16) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(new TextBlock { Text = project.Path, FontSize = 11, Foreground = ThemeBrush("MutedTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 7) });
            var search = new TextBox { ToolTip = "Filter project files", Padding = new Thickness(8, 6, 8, 6), Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1) };
            header.Children.Add(search);
            Grid.SetColumnSpan(header, 2); layout.Children.Add(header);
            var fileList = new ListBox { ItemsSource = files, Background = ThemeBrush("MainSurfaceAltBrush"), Foreground = ThemeBrush("MainTextBrush"), BorderBrush = ThemeBrush("MainBorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(4) };
            Grid.SetRow(fileList, 1); layout.Children.Add(fileList);
            var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(10), Margin = new Thickness(12, 0, 0, 0), Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("MainBorderBrush"), BorderThickness = new Thickness(1), ToolTip = "Read-only file preview" };
            Grid.SetRow(preview, 1); Grid.SetColumn(preview, 1); layout.Children.Add(preview);
            search.TextChanged += (_, _) => fileList.ItemsSource = files.Where(path => path.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            fileList.SelectionChanged += async (_, _) =>
            {
                if (fileList.SelectedItem is not string selected) { preview.Clear(); return; }
                try { preview.Text = await service.ReadFileAsync(selected); }
                catch (Exception ex) { preview.Text = $"Could not preview this file.\n\n{ex.Message}"; }
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var add = new Button { Content = "Add selected to chat context", Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            add.Click += async (_, _) =>
            {
                if (fileList.SelectedItem is not string selected || _active is null || !SameWorkspace(_active.ProjectPath, project.Path))
                {
                    MessageBox.Show(dialog, "Open a conversation in this project before adding context files.", "Project conversation required", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var projectSettings = EnsureProject(project.Path);
                if (new WorkspaceFileService(project.Path, projectSettings.ContextExclusions).IsContextExcluded(selected))
                {
                    MessageBox.Show(dialog, "This file is excluded from chat context by the project settings.", "Context exclusion", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!_active.ContextFiles.Contains(selected, StringComparer.OrdinalIgnoreCase)) _active.ContextFiles.Add(selected);
                UpdateContextLabel(_active);
                await SaveAsync();
            };
            fileList.SelectionChanged += (_, _) => add.IsEnabled = fileList.SelectedItem is string;
            var close = new Button { Content = "Close", Padding = new Thickness(13, 7, 13, 7), IsCancel = true };
            buttons.Children.Add(add); buttons.Children.Add(close);
            Grid.SetRow(buttons, 2); Grid.SetColumnSpan(buttons, 2); layout.Children.Add(buttons);
            dialog.Content = layout;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not browse this project folder.\n\n{ex.Message}", "Project files", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SearchProjectContents(WorkspaceProject project)
    {
        if (!Directory.Exists(project.Path))
        {
            MessageBox.Show(this, "The selected project folder no longer exists.", "Project folder missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var service = new WorkspaceFileService(project.Path, project.ContextExclusions);
        var dialog = new Window
        {
            Title = $"Search project contents · {project.Name}", Width = 940, Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"),
            ResizeMode = ResizeMode.CanResize
        };
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "Find a literal string in chat-context-eligible source files. Search skips configured exclusions and returns up to 50 matching lines.", TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 9) });
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition());
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var query = new TextBox { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(9, 5, 9, 5), Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1), ToolTip = "Literal text or code symbol" };
        var find = new Button { Content = "Find", Style = (Style)FindResource("SoftButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        Grid.SetColumn(find, 1); searchRow.Children.Add(query); searchRow.Children.Add(find); header.Children.Add(searchRow);
        var status = new TextBlock { Text = "Enter a term to search this project.", FontSize = 10, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 7, 0, 0) };
        header.Children.Add(status);
        Grid.SetColumnSpan(header, 2); layout.Children.Add(header);

        var matches = new ListBox { SelectionMode = SelectionMode.Extended, Background = ThemeBrush("MainSurfaceAltBrush"), Foreground = ThemeBrush("MainTextBrush"), BorderBrush = ThemeBrush("MainBorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(4), ToolTip = "Select one or more matching lines, then add their files to chat context" };
        Grid.SetRow(matches, 1); layout.Children.Add(matches);
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10), Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("MainBorderBrush"), BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        Grid.SetColumn(preview, 1); Grid.SetRow(preview, 1); layout.Children.Add(preview);
        var addFiles = new Button { Content = "Add selected files", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        matches.SelectionChanged += (_, _) =>
        {
            var selected = matches.SelectedItems.OfType<FileSearchMatch>().ToArray();
            preview.Text = selected.Length == 0 ? "Select a search result to inspect the match." : string.Join(Environment.NewLine + Environment.NewLine, selected.Take(8).Select(match => $"{match.RelativePath}:{match.LineNumber}{Environment.NewLine}{match.LineText}"));
            addFiles.IsEnabled = selected.Length > 0;
            var fileCount = selected.Select(match => match.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            addFiles.Content = fileCount > 0 ? $"Add {fileCount} file(s) to chat context" : "Add selected files";
        };

        var searching = false;
        async Task RunSearchAsync()
        {
            var term = query.Text.Trim();
            if (string.IsNullOrWhiteSpace(term) || searching) return;
            searching = true;
            find.IsEnabled = false;
            status.Text = "Searching project files…";
            try
            {
                var found = await service.SearchContextFilesAsync(term, cancellation.Token);
                matches.ItemsSource = found;
                status.Text = found.Count == 0 ? "No matches found." : $"{found.Count} matching line(s) in {found.Select(match => match.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} file(s).";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = "Search failed: " + ex.Message; }
            finally { searching = false; find.IsEnabled = true; }
        }
        find.Click += async (_, _) => await RunSearchAsync();
        query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await RunSearchAsync(); } };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        addFiles.Click += async (_, _) =>
        {
            if (_active is null || !SameWorkspace(_active.ProjectPath, project.Path))
            {
                MessageBox.Show(dialog, "Open a conversation in this project before adding context files.", "Project conversation required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var paths = matches.SelectedItems.OfType<FileSearchMatch>().Select(match => match.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in paths)
                if (!_active.ContextFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) _active.ContextFiles.Add(path);
            UpdateContextLabel(_active);
            await SaveAsync();
            status.Text = $"Added {paths.Length} selected file(s) to chat context.";
        };
        var close = new Button { Content = "Close", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(13, 7, 13, 7), IsCancel = true };
        buttons.Children.Add(addFiles); buttons.Children.Add(close);
        Grid.SetRow(buttons, 2); Grid.SetColumnSpan(buttons, 2); layout.Children.Add(buttons);
        dialog.Content = layout;
        dialog.Closed += (_, _) => cancellation.Cancel();
        dialog.ShowDialog();
    }

    private void FillConversationList(ItemsControl list, IEnumerable<Conversation> conversations)
    {
        list.Items.Clear();
        foreach (var item in conversations)
        {
            var title = string.IsNullOrWhiteSpace(item.Title) ? "New conversation" : item.Title;
            var button = new Button { Style = (Style)FindResource("SidebarButton"), Tag = item, Padding = new Thickness(11, 8, 7, 8), Margin = new Thickness(0, 1, 0, 1), Background = ReferenceEquals(item, _active) ? ThemeBrush("SidebarActiveBrush") : Brushes.Transparent };
            var row = new DockPanel();
            var requestStatus = ReferenceEquals(_activeRequestConversation, item)
                ? (_activeRequestIsCodeTask ? "Working · Code" : "Working")
                : item.PendingRequestCount > 0 ? $"Queued · {item.PendingRequestCount}" : "";
            if (!string.IsNullOrEmpty(requestStatus))
            {
                var badge = new TextBlock { Text = requestStatus, FontSize = 9, Foreground = ThemeBrush("WelcomeAccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
            }
            var modelLabel = FindModelOption(item.Model)?.DisplayName ?? item.Model;
            var shortModel = modelLabel.Split('·')[0].Trim();
            var details = new List<string> { shortModel };
            if (item.LastPromptTokens > 0)
            {
                var contextLimit = item.LastPromptContext > 0 ? item.LastPromptContext : item.NumCtx > 0 ? item.NumCtx : MaxContextForModel(item.LastPromptModel.Length > 0 ? item.LastPromptModel : item.Model);
                details.Add($"{FormatTokenCount(item.LastPromptTokens)}/{FormatContextLimit(contextLimit)}");
            }
            if (!string.IsNullOrWhiteSpace(item.ProjectPath)) details.Add(Path.GetFileName(item.ProjectPath));
            if (item.FileChanges.Count > 0) details.Add($"{item.FileChanges.Count} changes");
            details.Add(FormatRelativeTime(item.UpdatedAt));
            var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            caption.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 188, FontSize = 12, Foreground = ThemeBrush("SidebarTextBrush") });
            caption.Children.Add(new TextBlock { Text = string.Join(" · ", details), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 188, FontSize = 9, Foreground = ThemeBrush("SidebarMutedBrush"), Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(caption);
            var contextText = item.LastPromptTokens > 0 ? $"Last prompt: {FormatTokenCount(item.LastPromptTokens)} / {FormatContextLimit(item.LastPromptContext > 0 ? item.LastPromptContext : item.NumCtx > 0 ? item.NumCtx : MaxContextForModel(item.LastPromptModel.Length > 0 ? item.LastPromptModel : item.Model))}" : "No prompt usage reported yet";
            button.ToolTip = $"{title}\nModel: {modelLabel}\nWorkspace: {item.ProjectPath ?? "Quick chat"}\n{contextText}\nChanged files: {item.FileChanges.Count}\nLast activity: {item.UpdatedAt.LocalDateTime:g}\n{requestStatus}";
            button.Content = row;
            button.Click += (_, _) => SelectConversation(item);
            var menu = new ContextMenu();
            var pin = new MenuItem { Header = item.IsPinned ? "Unpin conversation" : "Pin conversation" };
            pin.Click += (_, _) => { item.IsPinned = !item.IsPinned; RefreshConversationLists(); _ = SaveAsync(); };
            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) => await RenameConversationAsync(item);
            var export = new MenuItem { Header = "Export as Markdown…" };
            export.Click += async (_, _) => await ExportConversationAsync(item);
            var archive = new MenuItem { Header = item.IsArchived ? "Restore to conversations" : "Archive conversation" };
            archive.Click += async (_, _) => await SetConversationArchivedAsync(item, !item.IsArchived);
            menu.Items.Add(pin);
            menu.Items.Add(rename);
            menu.Items.Add(export);
            menu.Items.Add(new Separator());
            menu.Items.Add(archive);
            if (item.PendingRequestCount > 0)
            {
                var cancelQueued = new MenuItem { Header = $"Cancel {item.PendingRequestCount} queued request(s)" };
                cancelQueued.Click += async (_, _) => await CancelQueuedRequestsAsync(item);
                menu.Items.Add(cancelQueued);
            }
            if (item.IsArchived)
            {
                var delete = new MenuItem { Header = "Delete permanently…" };
                delete.Click += async (_, _) => await DeleteConversationAsync(item);
                menu.Items.Add(delete);
            }
            button.ContextMenu = menu;
            list.Items.Add(button);
        }
        if (list.Items.Count == 0) list.Items.Add(new TextBlock { Text = "Nothing here yet", FontSize = 11, Foreground = ThemeBrush("SidebarMutedBrush"), Margin = new Thickness(12, 3, 0, 3) });
    }

    private void ToggleArchiveView_Click(object sender, RoutedEventArgs e)
    {
        var nextViewIsArchived = !_showArchived;
        var available = _conversations.Where(c => SameWorkspace(c.ProjectPath, _activeProject?.Path) && c.IsArchived == nextViewIsArchived).ToList();
        if (available.Count == 0)
        {
            MessageBox.Show(this, nextViewIsArchived ? "There are no archived conversations in this project." : "There are no active conversations in this project.", "Conversations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _showArchived = nextViewIsArchived;
        ArchiveViewButton.Content = _showArchived ? "←  Show active chats" : "◷  Show archived";
        SelectConversation(available.OrderByDescending(c => c.UpdatedAt).First());
    }

    private async Task RenameConversationAsync(Conversation conversation)
    {
        var dialog = new Window { Title = "Rename conversation", Width = 460, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"), ResizeMode = ResizeMode.NoResize };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Save name", Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var input = new TextBox { Text = conversation.Title, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(9, 7, 9, 7), Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
        layout.Children.Add(input);
        save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(input.Text)) return; conversation.Title = input.Text.Trim(); dialog.DialogResult = true; dialog.Close(); };
        dialog.Content = layout;
        if (dialog.ShowDialog() == true)
        {
            if (ReferenceEquals(_active, conversation)) ConversationTitle.Text = conversation.Title;
            conversation.UpdatedAt = DateTimeOffset.Now;
            RefreshConversationLists();
            await SaveAsync();
        }
    }

    private async Task SetConversationArchivedAsync(Conversation conversation, bool archived)
    {
        if (conversation.PendingRequestCount > 0 || ReferenceEquals(_activeRequestConversation, conversation))
        {
            MessageBox.Show(this, "Wait for this conversation’s active and queued requests to finish before archiving it.", "Conversation is busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        conversation.IsArchived = archived;
        conversation.UpdatedAt = DateTimeOffset.Now;
        if (ReferenceEquals(_active, conversation))
        {
            if (!archived && _showArchived)
            {
                _showArchived = false;
                ArchiveViewButton.Content = "◷  Show archived";
            }
            var next = _conversations.Where(c => !ReferenceEquals(c, conversation) && SameWorkspace(c.ProjectPath, _activeProject?.Path) && c.IsArchived == _showArchived).OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
            if (next is not null) SelectConversation(next);
            else if (archived)
            {
                _showArchived = false;
                ArchiveViewButton.Content = "◷  Show archived";
                NewChat_Click(this, new RoutedEventArgs());
            }
            else SelectConversation(conversation);
        }
        RefreshConversationLists();
        await SaveAsync();
    }

    private async Task DeleteConversationAsync(Conversation conversation)
    {
        if (conversation.PendingRequestCount > 0 || ReferenceEquals(_activeRequestConversation, conversation))
        {
            MessageBox.Show(this, "Wait for this conversation’s active and queued requests to finish before deleting it.", "Conversation is busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this, $"Permanently delete ‘{conversation.Title}’, its history, and its local file checkpoints?", "Delete conversation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        var wasActive = ReferenceEquals(_active, conversation);
        _conversations.Remove(conversation);
        var checkpointDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", conversation.Id.ToString("N"));
        try { if (Directory.Exists(checkpointDirectory)) Directory.Delete(checkpointDirectory, recursive: true); } catch { }
        if (wasActive)
        {
            var next = _conversations.Where(c => SameWorkspace(c.ProjectPath, _activeProject?.Path) && c.IsArchived == _showArchived).OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
            if (next is not null) SelectConversation(next);
            else
            {
                _showArchived = false;
                ArchiveViewButton.Content = "◷  Show archived";
                NewChat_Click(this, new RoutedEventArgs());
            }
        }
        RefreshConversationLists();
        await SaveAsync();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_requestCancellation is not null && ReferenceEquals(_activeRequestConversation, _active))
        {
            _requestCancellation.Cancel();
            AgentStatusLabel.Text = "Stopping…";
            return;
        }
        await SendPromptAsync();
    }

    private void ToggleCodeTask_Click(object sender, RoutedEventArgs e)
    {
        if (!_codeTaskMode && string.IsNullOrWhiteSpace(_active?.ProjectPath))
        {
            MessageBox.Show(this, "Open or attach a project folder before starting a code task.", "Project required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_codeTaskMode && _codeTaskConversationId != _active?.Id)
        {
            MessageBox.Show(this, "Code task mode is enabled for a different conversation. Turn it off, then enable it for this project conversation.", "Code task mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _codeTaskMode = !_codeTaskMode;
        _planMode = false;
        _codeTaskConversationId = _codeTaskMode ? _active?.Id : null;
        UpdateModeButtons();
    }

    private void TogglePlanMode_Click(object sender, RoutedEventArgs e)
    {
        _planMode = !_planMode;
        _codeTaskMode = false;
        _codeTaskConversationId = null;
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        PlanModeButton.Content = _planMode ? "◆  Plan on" : "◇  Plan";
        PlanModeButton.Background = _planMode ? ThemeBrush("AgentModeOnBrush") : ThemeBrush("SecondaryButtonBrush");
        PlanModeButton.ToolTip = _planMode ? "Plan mode is read-only; no file or command tools are available." : "Ask for a read-only implementation plan.";
        CodeTaskButton.Content = _codeTaskMode ? "◆  Code task on" : "◇  Code task";
        CodeTaskButton.Background = _codeTaskMode ? ThemeBrush("AgentModeOnBrush") : ThemeBrush("SecondaryButtonBrush");
        CodeTaskButton.ToolTip = _codeTaskMode
            ? "Code task mode is on: project file tools are available; every change and command needs your approval."
            : "Chat mode is read-only. Enable Code task for reviewed project changes.";
    }

    private async Task SendPromptAsync()
    {
        var text = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || _active is null) return;
        var isCodeTask = _codeTaskMode && _codeTaskConversationId == _active.Id;
        var isPlanMode = _planMode;
        if (ModelPicker.SelectedValue is string model) _active.Model = model;
        var conversation = _active;
        var userMessage = text;
        if (conversation.Messages.Count == 0)
        {
            conversation.Title = MakeTitle(userMessage);
            ConversationTitle.Text = conversation.Title;
        }
        conversation.Messages.Add(new ChatMessage("user", userMessage));
        var assistantIndex = conversation.Messages.Count;
        conversation.Messages.Add(new ChatMessage("assistant", ""));
        conversation.UpdatedAt = DateTimeOffset.Now;
        conversation.PendingRequestCount++;
        List<string> exclusions = conversation.ProjectPath is null ? [] : [.. EnsureProject(conversation.ProjectPath).ContextExclusions];
        var turn = new QueuedTurn(conversation, assistantIndex, conversation.Model, conversation.NumCtx,
            isCodeTask, isPlanMode, conversation.ProjectPath, [.. conversation.ContextFiles], exclusions);
        PromptBox.Clear();
        WelcomePanel.Visibility = Visibility.Collapsed;
        RenderMessages();
        RefreshConversationLists();
        UpdateSendControl();
        UpdateActiveRequestStatus();
        await SaveAsync();
        if (_isClosing)
        {
            conversation.Messages[assistantIndex] = new ChatMessage("assistant", "Queued request was not sent before Codev closed.");
            return;
        }
        _requestQueue.Enqueue(turn);
        UpdateQueueControl();
        await ProcessQueuedTurnsAsync();
    }

    private async Task ProcessQueuedTurnsAsync()
    {
        UpdateQueueControl();
        if (_isClosing || _queuePaused) { UpdateActiveRequestStatus(); return; }
        try
        {
            await _requestQueue.ProcessPendingAsync(async turn =>
            {
                turn.Conversation.PendingRequestCount = Math.Max(0, turn.Conversation.PendingRequestCount - 1);
                RefreshConversationLists();
                UpdateQueueControl();
                await ExecuteQueuedTurnAsync(turn);
            }, async (turn, error) =>
            {
                if (turn.AssistantIndex < turn.Conversation.Messages.Count)
                    turn.Conversation.Messages[turn.AssistantIndex] = new ChatMessage("assistant", $"Could not complete the queued request.\n\n{error.Message}");
                turn.Conversation.UpdatedAt = DateTimeOffset.Now;
                await SaveAsync();
            }, () => !_queuePaused && !_isClosing);
        }
        finally
        {
            if (_requestQueue.Count == 0) _queuePaused = false;
            UpdateSendControl();
            UpdateActiveRequestStatus();
            UpdateQueueControl();
        }
    }

    private void ToggleQueuePause_Click(object sender, RoutedEventArgs e)
    {
        if (_requestQueue.Count == 0 || _isClosing) return;
        _queuePaused = !_queuePaused;
        UpdateQueueControl();
        UpdateActiveRequestStatus();
        if (!_queuePaused) _ = ProcessQueuedTurnsAsync();
    }

    private async void ConversationBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Conversation backup", Width = 430, Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"),
            ResizeMode = ResizeMode.NoResize
        };
        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock
        {
            Text = "Save conversation history to a local JSON file, or add chats from an existing Codev backup. Project folder paths may be included, but project files and rollback checkpoint contents are not. Imported chats get new IDs and do not replace your current history.",
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 16)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var export = new Button { Content = "Create backup…", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        var import = new Button { Content = "Import backup…", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        export.Click += (_, _) => { dialog.Tag = "export"; dialog.DialogResult = true; dialog.Close(); };
        import.Click += (_, _) => { dialog.Tag = "import"; dialog.DialogResult = true; dialog.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(export); buttons.Children.Add(import);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        if (dialog.ShowDialog() != true) return;
        if (dialog.Tag as string == "export") await ExportConversationBackupAsync();
        else await ImportConversationBackupAsync();
    }

    private async Task ExportConversationBackupAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create Codev conversation backup", FileName = $"codev-backup-{DateTime.Now:yyyy-MM-dd}.codev.json",
            DefaultExt = ".codev.json", AddExtension = false,
            Filter = "Codev conversation backup (*.codev.json)|*.codev.json|JSON file (*.json)|*.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await SaveAsync();
        try
        {
            var backup = ConversationBackupService.Export(_conversations, JsonOptions);
            await File.WriteAllTextAsync(dialog.FileName, backup, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AgentStatusLabel.Text = $"Backup saved · {_conversations.Count} conversation(s)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not create the conversation backup.\n\n{ex.Message}", "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ImportConversationBackupAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Codev conversation backup", CheckFileExists = true,
            Filter = "Codev conversation backup (*.codev.json;*.json)|*.codev.json;*.json|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 100_000_000)
                throw new InvalidDataException("The selected backup is larger than the 100 MB import limit.");
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var imported = ConversationBackupService.Import(json, JsonOptions);
            foreach (var conversation in imported.OrderBy(c => c.UpdatedAt))
            {
                conversation.IsArchived = false;
                conversation.PendingRequestCount = 0;
                if (!string.IsNullOrWhiteSpace(conversation.ProjectPath))
                {
                    if (Directory.Exists(conversation.ProjectPath)) EnsureProject(conversation.ProjectPath);
                    else conversation.ProjectPath = null;
                }
                _conversations.Insert(0, conversation);
            }
            _showArchived = false;
            _activeProject = null;
            ArchiveViewButton.Content = "◷  Show archived";
            RefreshConversationLists();
            if (imported.Count > 0) SelectConversation(imported.OrderByDescending(c => c.UpdatedAt).First());
            await SaveAsync();
            MessageBox.Show(this, $"Imported {imported.Count} conversation(s). Existing history was left unchanged.", "Backup imported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not import this conversation backup. Your existing conversations were left unchanged.\n\n{ex.Message}", "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateQueueControl()
    {
        if (_requestQueue.Count == 0) _queuePaused = false;
        QueueControlButton.Visibility = _requestQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueControlButton.Content = _queuePaused ? "▶ Resume queue" : "Ⅱ Pause queue";
        QueueControlButton.ToolTip = _queuePaused
            ? "Resume queued local model requests"
            : "Let the current Ollama response finish, then pause queued requests";
        QueueControlButton.IsEnabled = !_isClosing;
    }

    private async Task ExecuteQueuedTurnAsync(QueuedTurn turn)
    {
        var conversation = turn.Conversation;
        var assistantIndex = turn.AssistantIndex;
        var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        _activeRequestConversation = conversation;
        _activeRequestIsCodeTask = turn.IsCodeTask;
        RefreshConversationLists();
        UpdateSendControl();
        UpdateActiveRequestStatus();
        try
        {
            var history = conversation.Messages.Take(assistantIndex).Select(m => new OllamaMessage(m.Role, m.Content)).ToList();
            var system = turn.IsCodeTask
                ? "You are Codev, a concise local coding agent. Work only within the selected project. Inspect before editing. Use the provided tools instead of claiming actions. Every file replacement and shell command requires user approval. Never represent tool output as successful unless its result confirms success."
                : turn.IsPlanMode
                    ? "You are Codev in read-only Plan mode. Give a concise, ordered implementation plan with key files, risks, and checks. Do not edit files, run commands, or claim that any work has been done. Ask a short clarifying question only if a missing detail blocks a useful plan."
                    : "You are Codev, a practical coding assistant. Be concise, explain decisions plainly, and focus on useful implementation details. The user is chatting through a local desktop app. Do not claim you changed files or ran commands; this mode is read-only.";
            var project = conversation.ProjectPath is null ? null : EnsureProject(conversation.ProjectPath);
            if (project is not null && !string.IsNullOrWhiteSpace(project.Instructions))
                system += "\n\nProject-specific instructions (apply within this workspace):\n" + project.Instructions;
            if (!string.IsNullOrWhiteSpace(turn.ProjectPath) && !turn.IsCodeTask)
            {
                system += "\n\nThe user attached this local project folder: " + turn.ProjectPath + ". Project files are read-only context in this chat. Do not claim to have changed them.";
                system += "\n\n" + await CollectProjectContextAsync(turn.ProjectPath, cancellation.Token, turn.ContextFiles, turn.ContextExclusions);
            }
            history.Insert(0, new OllamaMessage("system", system));
            if (turn.IsCodeTask)
            {
                var service = new WorkspaceFileService(turn.ProjectPath!);
                await RunAgentTurnAsync(conversation, assistantIndex, history, service, turn.Model, turn.NumCtx, cancellation.Token);
            }
            else
                await RunChatTurnAsync(conversation, assistantIndex, history, turn.Model, turn.NumCtx, cancellation.Token);
            if (string.IsNullOrWhiteSpace(conversation.Messages[assistantIndex].Content))
                conversation.Messages[assistantIndex] = new ChatMessage("assistant", "The model returned an empty response. Check that the selected model is installed and running in Ollama.");
        }
        catch (OperationCanceledException)
        {
            var partial = conversation.Messages[assistantIndex].Content;
            conversation.Messages[assistantIndex] = new ChatMessage("assistant", string.IsNullOrWhiteSpace(partial) ? "Generation stopped." : partial + "\n\n[Generation stopped.]");
        }
        catch (Exception ex) { conversation.Messages[assistantIndex] = new ChatMessage("assistant", $"Could not complete the request.\n\n{ex.Message}\n\nCheck that Ollama is running and that this model is installed."); }
        finally
        {
            conversation.UpdatedAt = DateTimeOffset.Now;
            await SaveAsync();
            cancellation.Dispose();
            _requestCancellation = null;
            _activeRequestConversation = null;
            _activeRequestIsCodeTask = false;
            RefreshConversationLists();
            if (ReferenceEquals(_active, conversation)) RenderMessages();
            RefreshConversationLists();
            UpdateSendControl();
            UpdateActiveRequestStatus();
        }
    }

    private async Task RunChatTurnAsync(Conversation conversation, int assistantIndex, List<OllamaMessage> history, string model, int numCtx, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(BuildChatPayload(model, numCtx, history, stream: true)) };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            if (json.RootElement.TryGetProperty("prompt_eval_count", out var promptCount) && promptCount.TryGetInt32(out var promptTokens))
            {
                conversation.LastPromptTokens = promptTokens;
                conversation.LastPromptContext = numCtx;
                conversation.LastPromptModel = model;
                UpdateContextUsage(conversation);
            }
            if (json.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var chunk))
            {
                output.Append(chunk.GetString());
                conversation.Messages[assistantIndex] = new ChatMessage("assistant", output.ToString());
                if (ReferenceEquals(_active, conversation)) RenderMessages();
            }
        }
    }

    private async Task RunAgentTurnAsync(Conversation conversation, int assistantIndex, List<OllamaMessage> history, WorkspaceFileService service, string model, int numCtx, CancellationToken cancellationToken)
    {
        var tools = new object[]
        {
            Tool("list_files", "List project files; pass a project-relative directory or an empty string for the root.", new { relative_directory = new { type = "string" } }, ["relative_directory"]),
            Tool("read_file", "Read a UTF-8 text file from the selected project.", new { relative_path = new { type = "string" } }, ["relative_path"]),
            Tool("search_files", "Search supported source files for a literal string.", new { query = new { type = "string" } }, ["query"]),
            Tool("create_file", "Propose a new source, text, or configuration file in an existing project folder. User approval is required.", new { relative_path = new { type = "string" }, content = new { type = "string" } }, ["relative_path", "content"]),
            Tool("write_file", "Propose the complete replacement contents of one existing project file. User approval is required.", new { relative_path = new { type = "string" }, content = new { type = "string" } }, ["relative_path", "content"]),
            Tool("run_command", "Request approval to run one PowerShell command in the project folder. Every invocation requires approval.", new { command = new { type = "string" } }, ["command"])
        };
        for (var round = 0; round < 8; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
            {
                Content = JsonContent.Create(BuildChatPayload(model, numCtx, history, stream: false, tools))
            };
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            if (json.RootElement.TryGetProperty("prompt_eval_count", out var promptCount) && promptCount.TryGetInt32(out var promptTokens))
            {
                conversation.LastPromptTokens = promptTokens;
                conversation.LastPromptContext = numCtx;
                conversation.LastPromptModel = model;
                UpdateContextUsage(conversation);
            }
            var message = json.RootElement.GetProperty("message");
            var text = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? "" : "";
            var calls = message.TryGetProperty("tool_calls", out var callsElement) && callsElement.ValueKind == JsonValueKind.Array
                ? callsElement.EnumerateArray().ToList() : [];
            if (calls.Count == 0)
            {
                conversation.Messages[assistantIndex] = new ChatMessage("assistant", text);
                RenderAgentTranscript(conversation);
                return;
            }

            history.Add(new OllamaMessage("assistant", text, calls.Select(call => JsonSerializer.Deserialize<JsonElement>(call.GetRawText())).ToList()));
            var assistantText = new StringBuilder(text);
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? "";
                SetAgentStatus(conversation, $"Code task · {name.Replace('_', ' ')}");
                var arguments = function.TryGetProperty("arguments", out var args) ? args : default;
                var result = await ExecuteAgentToolAsync(name, arguments, service, conversation, cancellationToken);
                history.Add(new OllamaMessage("tool", result, null, name));
                assistantText.Append("\n\n").Append("Tool ").Append(name).Append(": ").Append(result.Length > 1400 ? result[..1400] + "… [truncated in transcript]" : result);
            }
            SetAgentStatus(conversation, "Code task · Thinking…");
            conversation.Messages[assistantIndex] = new ChatMessage("assistant", assistantText.ToString());
            RenderAgentTranscript(conversation);
        }
        throw new InvalidOperationException("The agent reached the eight-step tool limit. Send a follow-up to continue.");
    }

    private static Dictionary<string, object> BuildChatPayload(string model, int numCtx, List<OllamaMessage> messages, bool stream, object[]? tools = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream
        };
        if (tools is not null) payload["tools"] = tools;
        if (numCtx > 0) payload["options"] = new OllamaOptions(numCtx);
        return payload;
    }

    private async Task<string> ExecuteAgentToolAsync(string name, JsonElement arguments, WorkspaceFileService service, Conversation conversation, CancellationToken cancellationToken)
    {
        string Arg(string key) => arguments.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        try
        {
            return name switch
            {
                "list_files" => string.Join("\n", service.ListFiles(Arg("relative_directory"), 160)),
                "read_file" => await service.ReadFileAsync(Arg("relative_path"), cancellationToken),
                "search_files" => string.Join("\n", await service.SearchFilesAsync(Arg("query"), cancellationToken)),
                "create_file" => await ReviewAndCreateFileAsync(Arg("relative_path"), Arg("content"), service, conversation, cancellationToken),
                "write_file" => await ReviewAndWriteFileAsync(Arg("relative_path"), Arg("content"), service, conversation, cancellationToken),
                "run_command" => await ApproveAndRunCommandAsync(Arg("command"), service, cancellationToken),
                _ => "Error: tool is not available."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "Error: " + ex.Message; }
    }

    private async Task<string> ReviewAndCreateFileAsync(string relativePath, string proposed, WorkspaceFileService service, Conversation conversation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "Error: a project-relative path is required.";
        var full = service.ResolvePath(relativePath);
        if (File.Exists(full)) return "Rejected: a file already exists here. Use write_file to propose an edit instead.";
        if (!ShowFileReview(relativePath, "[New file]", proposed, isNewFile: true)) return "Rejected by user; no file was created.";
        await service.CreateFileAtomicAsync(relativePath, proposed, cancellationToken);
        conversation.FileChanges.Add(new FileChangeRecord(relativePath, null, DateTimeOffset.Now, "Create", PreviousFileExisted: false));
        if (ReferenceEquals(_active, conversation)) UpdateChangesButton(conversation);
        return "Approved and created the new project file.";
    }

    private async Task<string> ReviewAndWriteFileAsync(string relativePath, string proposed, WorkspaceFileService service, Conversation conversation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "Error: a project-relative path is required.";
        if (!File.Exists(service.ResolvePath(relativePath))) return "Rejected: creating new files is not available yet; propose a change to an existing file.";
        var snapshot = await service.ReadFileSnapshotAsync(relativePath, cancellationToken);
        if (!ShowFileReview(relativePath, snapshot.Content, proposed)) return "Rejected by user; the file was left unchanged.";
        var checkpoint = await service.CreateCheckpointAsync(relativePath, conversation.Id, cancellationToken, snapshot.Sha256);
        await service.WriteFileAtomicAsync(relativePath, proposed, cancellationToken, snapshot.Sha256);
        if (checkpoint is not null)
        {
            conversation.FileChanges.Add(new FileChangeRecord(relativePath, checkpoint, DateTimeOffset.Now, "Edit"));
            if (ReferenceEquals(_active, conversation)) UpdateChangesButton(conversation);
        }
        return $"Approved and applied. Original backed up at {checkpoint ?? "(new file)"}.";
    }

    private async Task<string> ApproveAndRunCommandAsync(string command, WorkspaceFileService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command)) return "Error: command is empty.";
        if (command.Length > 4000) return "Rejected: command exceeds 4,000 characters.";
        if (!ShowCommandApproval(command, service.Root)) return "Rejected by user; command was not run.";
        return await service.RunApprovedCommandAsync(command, TimeSpan.FromMinutes(3), cancellationToken);
    }

    private bool ShowCommandApproval(string command, string projectPath)
    {
        var dialog = new Window { Title = "Approve project command", Width = 760, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"), ResizeMode = ResizeMode.CanResize, SizeToContent = SizeToContent.Manual };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var warning = new TextBlock
        {
            Text = "This command runs through PowerShell as your Windows account. It can access files and services available to that account; Codev cannot sandbox shell commands to the project folder. Review the command before approving.",
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 12)
        };
        layout.Children.Add(warning);
        var cwd = new TextBlock { Text = "Working directory: " + projectPath, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(cwd, 1); layout.Children.Add(cwd);
        var commandBox = new TextBox { Text = command, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
        Grid.SetRow(commandBox, 2); layout.Children.Add(commandBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reject = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var approve = new Button { Content = "Approve & run", Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        approve.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        buttons.Children.Add(reject); buttons.Children.Add(approve); Grid.SetRow(buttons, 3); layout.Children.Add(buttons);
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }

    private bool ShowFileReview(string relativePath, string before, string after, bool isNewFile = false)
    {
        var dialog = new Window { Title = $"{(isNewFile ? "Review new file" : "Review change")} · {relativePath}", Width = 940, Height = 660, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"), ResizeMode = ResizeMode.CanResize };
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        var viewButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        var sideBySideButton = new Button { Content = "Side by side", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 5, 0) };
        var unifiedButton = new Button { Content = "Unified diff", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(viewButtons, Dock.Right);
        viewButtons.Children.Add(sideBySideButton); viewButtons.Children.Add(unifiedButton);
        header.Children.Add(viewButtons);
        var note = new TextBlock { Text = isNewFile ? "No file exists at this path. Approving creates it in the selected project folder." : "Review the proposed state. Approving applies it after saving a local checkpoint.", TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(note);
        Grid.SetColumnSpan(header, 2); layout.Children.Add(header);
        TextBox ReviewBox(string value) => new() { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(8) };
        var oldBox = ReviewBox(isNewFile ? "" : before); var newBox = ReviewBox(after);
        var sideBySide = new Grid();
        sideBySide.ColumnDefinitions.Add(new ColumnDefinition()); sideBySide.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(oldBox, 0); Grid.SetColumn(newBox, 1); sideBySide.Children.Add(oldBox); sideBySide.Children.Add(newBox);
        var diffDocument = new FlowDocument { PagePadding = new Thickness(8), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        var diffBox = new RichTextBox { IsReadOnly = true, IsDocumentEnabled = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = ThemeBrush("ComposerBrush"), Foreground = ThemeBrush("InputTextBrush"), BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1), Document = diffDocument, Visibility = Visibility.Collapsed };
        var diffBefore = isNewFile ? "" : before;
        var diffLines = UnifiedDiff.Compare(diffBefore, after);
        var formattedDiff = UnifiedDiff.Format(relativePath, diffBefore, after).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        foreach (var heading in formattedDiff.Take(3))
            diffDocument.Blocks.Add(new Paragraph(new Run(heading)) { Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var hasChanges = diffLines.Any(line => line.Kind != UnifiedDiffLineKind.Context);
        if (hasChanges)
        {
            foreach (var line in diffLines)
            {
                var prefix = line.Kind switch { UnifiedDiffLineKind.Added => "+", UnifiedDiffLineKind.Removed => "-", _ => " " };
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                var brush = line.Kind switch
                {
                    UnifiedDiffLineKind.Added => new SolidColorBrush(Color.FromRgb(129, 199, 132)),
                    UnifiedDiffLineKind.Removed => new SolidColorBrush(Color.FromRgb(239, 154, 154)),
                    _ => ThemeBrush("InputTextBrush")
                };
                paragraph.Inlines.Add(new Run(prefix + line.Text) { Foreground = brush });
                diffDocument.Blocks.Add(paragraph);
            }
        }
        void SetReviewView(bool unified)
        {
            sideBySide.Visibility = unified ? Visibility.Collapsed : Visibility.Visible;
            diffBox.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
            sideBySideButton.Background = unified ? ThemeBrush("SecondaryButtonBrush") : ThemeBrush("WelcomeAccentBackgroundBrush");
            unifiedButton.Background = unified ? ThemeBrush("WelcomeAccentBackgroundBrush") : ThemeBrush("SecondaryButtonBrush");
        }
        sideBySideButton.Click += (_, _) => SetReviewView(false);
        unifiedButton.Click += (_, _) => SetReviewView(true);
        SetReviewView(false);
        Grid.SetRow(sideBySide, 1); Grid.SetColumnSpan(sideBySide, 2); layout.Children.Add(sideBySide);
        Grid.SetRow(diffBox, 1); Grid.SetColumnSpan(diffBox, 2); layout.Children.Add(diffBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reject = new Button { Content = "Keep unchanged", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var approve = new Button { Content = isNewFile ? "Approve & create" : "Approve & apply", Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        approve.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        buttons.Children.Add(reject); buttons.Children.Add(approve); Grid.SetRow(buttons, 2); Grid.SetColumnSpan(buttons, 2); layout.Children.Add(buttons);
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }

    private static object Tool(string name, string description, object properties, string[] required) => new
    {
        type = "function",
        function = new { name, description, parameters = new { type = "object", properties, required } }
    };

    private void RenderAgentTranscript(Conversation conversation)
    {
        if (ReferenceEquals(_active, conversation)) RenderMessages();
    }

    private void UpdateChangesButton(Conversation conversation)
    {
        var count = conversation.FileChanges?.Count ?? 0;
        ChangesButton.Content = count == 0 ? "Files" : $"Files · {count}";
        ChangesButton.IsEnabled = count > 0;
    }

    private void UpdateSendControl()
    {
        var stoppingThis = _requestCancellation is not null && ReferenceEquals(_activeRequestConversation, _active);
        SendButton.IsEnabled = true;
        SendButton.Content = stoppingThis ? "■" : "↑";
        SendButton.ToolTip = stoppingThis ? "Stop this response" : "Send message (Enter)";
    }

    private void UpdateActiveRequestStatus()
    {
        if (_active is null)
        {
            AgentStatusLabel.Text = _queuePaused ? $"Queue paused · {_requestQueue.Count} request(s) waiting" : "Your conversations and model requests stay on this device.";
            return;
        }
        if (_requestCancellation is not null && ReferenceEquals(_activeRequestConversation, _active))
            AgentStatusLabel.Text = (_activeRequestIsCodeTask ? "Code task · Running locally…" : "Generating locally…") + (_queuePaused && _requestQueue.Count > 0 ? " · queue paused" : "");
        else if (_queuePaused && _requestQueue.Count > 0)
            AgentStatusLabel.Text = $"Queue paused · {_requestQueue.Count} request(s) waiting";
        else if (_active.PendingRequestCount > 0)
            AgentStatusLabel.Text = $"Queued · {_active.PendingRequestCount} request(s) waiting for Ollama";
        else AgentStatusLabel.Text = "Your conversations and model requests stay on this device.";
    }

    private void SetAgentStatus(Conversation conversation, string status)
    {
        if (ReferenceEquals(_active, conversation)) AgentStatusLabel.Text = status;
    }

    private void ReviewChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { FileChanges.Count: > 0 } conversation) return;
        var dialog = new Window { Title = "Changed files", Width = 540, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"), ResizeMode = ResizeMode.CanResize };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var intro = new TextBlock { Text = "Changes approved in this conversation. Select an entry to compare the current file with its saved checkpoint and optionally restore it.", TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(intro, Dock.Top); layout.Children.Add(intro);
        var list = new StackPanel();
        foreach (var change in conversation.FileChanges.OrderByDescending(c => c.ChangedAt).ToArray())
        {
            var item = new Button { Style = (Style)FindResource("SidebarButton"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 2, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = change.RelativePath, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("MainTextBrush") });
            card.Children.Add(new TextBlock { Text = $"{change.Kind} · {change.ChangedAt.LocalDateTime:g}", FontSize = 11, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 3, 0, 0) });
            item.Content = card;
            if (string.IsNullOrWhiteSpace(change.CheckpointPath))
            {
                item.IsEnabled = false;
                item.ToolTip = "The conversation backup records this change but does not include its local rollback checkpoint.";
            }
            item.Click += async (_, _) => { dialog.Close(); await ReviewAndRestoreChangeAsync(conversation, change); };
            list.Children.Add(item);
        }
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    private async Task ReviewAndRestoreChangeAsync(Conversation conversation, FileChangeRecord change)
    {
        if (conversation.ProjectPath is null) return;
        try
        {
            var service = new WorkspaceFileService(conversation.ProjectPath);
            var currentExists = File.Exists(service.ResolvePath(change.RelativePath));
            var current = currentExists ? await service.ReadFileSnapshotAsync(change.RelativePath) : null;
            var previous = change.PreviousFileExisted
                ? await service.ReadCheckpointAsync(change.RelativePath, conversation.Id, change.CheckpointPath ?? "")
                : "[This restore will delete the file]";
            if (!ShowFileReview(change.RelativePath, current?.Content ?? "[The file does not currently exist]", previous)) return;
            var rollback = await service.RestoreFileStateAsync(change.RelativePath, conversation.Id, change.PreviousFileExisted, change.CheckpointPath, current?.Sha256);
            conversation.FileChanges.Remove(change);
            conversation.FileChanges.Add(new FileChangeRecord(change.RelativePath, rollback, DateTimeOffset.Now, "Restore", currentExists));
            UpdateChangesButton(conversation);
            await SaveAsync();
            MessageBox.Show(this, $"Restored {change.RelativePath}. A checkpoint of the version that was replaced is available under Files.", "File restored", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not restore the checkpoint. The current file was left unchanged.\n\n{ex.Message}", "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string MakeTitle(string prompt)
    {
        var oneLine = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length > 36 ? oneLine[..33] + "…" : oneLine;
    }

    private async Task<string> CollectProjectContextAsync(string root, CancellationToken cancellationToken, IReadOnlyList<string>? selectedFiles = null, IReadOnlyList<string>? contextExclusions = null)
    {
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".csproj", ".sln", ".md", ".txt", ".json", ".js", ".jsx", ".ts", ".tsx", ".py", ".html", ".css", ".sql", ".xml", ".yml", ".yaml", ".toml", ".props", ".targets", ".ps1", ".sh", ".bat" };
            var fileService = new WorkspaceFileService(root, contextExclusions);
        var output = new StringBuilder("Selected project files (limited read-only excerpts):\n");
        var count = 0;
        const int maxFiles = 24;
        const int maxChars = 32000;
        const int maxFileChars = 2400;
        try
        {
            var files = selectedFiles is { Count: > 0 } ? selectedFiles : fileService.ListContextFiles(maxEntries: 300);
            foreach (var relative in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!allowedExtensions.Contains(Path.GetExtension(relative))) continue;
                if (fileService.IsContextExcluded(relative)) continue;
                try
                {
                    var content = await fileService.ReadFileAsync(relative, cancellationToken);
                    if (content.Length > maxFileChars) content = content[..maxFileChars] + "\n… [excerpt truncated]";
                    output.Append("\n--- ").Append(relative).AppendLine(" ---\n").AppendLine(content);
                    count++;
                    if (count >= maxFiles || output.Length >= maxChars) break;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
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

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape && _requestCancellation is not null && ReferenceEquals(_activeRequestConversation, _active))
        {
            _requestCancellation.Cancel();
            AgentStatusLabel.Text = "Stopping…";
            e.Handled = true;
        }
        else if (control && e.Key == Key.N)
        {
            NewChat_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && e.Key == Key.F)
        {
            SearchFocus_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _active is not null)
        {
            _ = RenameConversationAsync(_active);
            e.Handled = true;
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _active.IsPinned = !_active.IsPinned;
        PinButton.Content = _active.IsPinned ? "★  Pinned" : "☆  Pin";
        RefreshConversationLists();
        _ = SaveAsync();
    }

    private async void ExportConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) await ExportConversationAsync(_active);
    }

    private async Task ExportConversationAsync(Conversation conversation)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export conversation",
            Filter = "Markdown document (*.md)|*.md|Text file (*.txt)|*.txt",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = MakeExportFileName(conversation.Title),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, ConversationMarkdownExporter.Export(conversation), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetAgentStatus(conversation, $"Exported · {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export the conversation.\n\n{ex.Message}", "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CancelQueuedRequestsAsync(Conversation conversation)
    {
        var removed = _requestQueue.RemoveWhere(turn => ReferenceEquals(turn.Conversation, conversation));
        if (removed.Count == 0) return;
        foreach (var turn in removed)
        {
            conversation.PendingRequestCount = Math.Max(0, conversation.PendingRequestCount - 1);
            if (turn.AssistantIndex < conversation.Messages.Count)
                conversation.Messages[turn.AssistantIndex] = new ChatMessage("assistant", "Queued request canceled before it was sent.");
        }
        conversation.UpdatedAt = DateTimeOffset.Now;
        if (ReferenceEquals(_active, conversation)) RenderMessages();
        RefreshConversationLists();
        UpdateQueueControl();
        UpdateActiveRequestStatus();
        await SaveAsync();
    }

    private static string MakeExportFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string((string.IsNullOrWhiteSpace(title) ? "conversation" : title.Trim())
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '-' : character).ToArray()).Trim(' ', '.');
        if (safe.Length > 100) safe = safe[..100].TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(safe) ? "conversation" : safe;
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingModel || _active is null || ModelPicker.SelectedValue is not string model) return;
        _active.Model = model;
        var maxContext = MaxContextForModel(model);
        if (_active.NumCtx > maxContext) _active.NumCtx = 0;
        RefreshContextPicker(_active);
        UpdateContextBudgetLabel(_active);
        RefreshConversationLists();
        _ = SaveAsync();
    }

    private void ContextPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingContext || _active is null || ContextPicker.SelectedValue is not int context) return;
        _active.NumCtx = context;
        UpdateContextBudgetLabel(_active);
        RefreshConversationLists();
        _ = SaveAsync();
    }

    private void RefreshContextPicker(Conversation? conversation)
    {
        _updatingContext = true;
        var model = conversation?.Model ?? ModelPicker.SelectedValue as string ?? "devstral-small-2-64k";
        var max = MaxContextForModel(model);
        if (conversation is not null && conversation.NumCtx > max)
        {
            conversation.NumCtx = 0;
            _ = SaveAsync();
        }
        ContextPicker.ItemsSource = _contextSizes.Where(option => option.Value == 0 || option.Value <= max).ToList();
        ContextPicker.SelectedValue = conversation?.NumCtx ?? 0;
        if (ContextPicker.SelectedValue is null) ContextPicker.SelectedValue = 0;
        _updatingContext = false;
        if (conversation is not null) UpdateContextUsage(conversation);
    }

    private int MaxContextForModel(string model)
    {
        var option = FindModelOption(model);
        if (option?.DisplayName.StartsWith("Qwen3-Coder-Next", StringComparison.Ordinal) == true || IsSameModel(RemoveLatestTag(model), "qwen3-coder-next-q2-24k", "qwen3-coder-next:q2_k_l", "hf.co/bartowski/Qwen_Qwen3-Coder-Next-GGUF:Q2_K_L")) return 24_576;
        return 65_536;
    }

    private void UpdateContextUsage(Conversation conversation)
    {
        if (!ReferenceEquals(_active, conversation)) return;
        var model = conversation.LastPromptTokens > 0 && !string.IsNullOrWhiteSpace(conversation.LastPromptModel) ? conversation.LastPromptModel : conversation.Model;
        var limit = conversation.LastPromptContext > 0 ? conversation.LastPromptContext : conversation.NumCtx > 0 ? conversation.NumCtx : MaxContextForModel(model);
        ContextUsageLabel.Text = conversation.LastPromptTokens > 0 ? $"{FormatTokenCount(conversation.LastPromptTokens)} / {FormatContextLimit(limit)}" : "";
        ContextUsageLabel.ToolTip = conversation.LastPromptTokens > 0
            ? "Latest prompt and conversation history token count reported by Ollama. The denominator is the selected request context size."
            : "Ollama reports context use after the first response.";
    }

    private static string FormatTokenCount(int tokens) => tokens >= 1000 ? $"{tokens / 1000d:0.#}k" : tokens.ToString();
    private static string FormatContextLimit(int tokens) => $"{tokens / 1024d:0.#}K";

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
        if (_active is null) return;
        if (string.IsNullOrWhiteSpace(_active.ProjectPath))
        {
            var folder = new OpenFolderDialog { Title = "Choose a project folder", Multiselect = false };
            if (folder.ShowDialog(this) != true) return;
            _active.ProjectPath = folder.FolderName;
            _projectPath = folder.FolderName;
            _activeProject = EnsureProject(_projectPath);
            RefreshConversationLists();
        }
        if (!Directory.Exists(_active.ProjectPath))
        {
            MessageBox.Show(this, "The selected project folder no longer exists. Open the project folder again to continue.", "Project folder missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var picker = new OpenFileDialog
        {
            Title = "Choose project files to include as context", Multiselect = true, CheckFileExists = true,
            InitialDirectory = _active.ProjectPath,
            Filter = "Project text and source files|*.cs;*.xaml;*.csproj;*.sln;*.md;*.txt;*.json;*.js;*.jsx;*.ts;*.tsx;*.py;*.html;*.css;*.sql;*.xml;*.yml;*.yaml;*.toml;*.props;*.targets;*.ps1;*.sh;*.bat|All files|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        var projectSettings = EnsureProject(_active.ProjectPath!);
        var service = new WorkspaceFileService(_active.ProjectPath!, projectSettings.ContextExclusions);
        var availableFiles = service.ListContextFiles(maxEntries: 500).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in picker.FileNames)
        {
            var full = Path.GetFullPath(path);
            if (!WorkspaceFileService.IsPathWithinRoot(service.Root, full)) continue;
            var relative = Path.GetRelativePath(service.Root, full);
            try
            {
                service.ResolvePath(relative);
                if (!availableFiles.Contains(relative)) continue;
                if (!_active.ContextFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) _active.ContextFiles.Add(relative);
            }
            catch (UnauthorizedAccessException) { }
        }
        UpdateContextLabel(_active);
        RefreshConversationLists();
        _ = SaveAsync();
    }

    private void UpdateContextLabel(Conversation conversation)
    {
        _projectPath = conversation.ProjectPath;
        var selectedCount = conversation.ContextFiles.Count;
        var excludedCount = 0;
        if (conversation.ProjectPath is not null && Directory.Exists(conversation.ProjectPath) && selectedCount > 0)
        {
            var project = EnsureProject(conversation.ProjectPath);
            var contextService = new WorkspaceFileService(conversation.ProjectPath, project.ContextExclusions);
            excludedCount = conversation.ContextFiles.Count(contextService.IsContextExcluded);
        }
        ContextLabel.Text = conversation.ProjectPath is null
            ? "No project attached"
            : selectedCount == 0 ? Path.GetFileName(conversation.ProjectPath) : $"{Path.GetFileName(conversation.ProjectPath)} · {selectedCount - excludedCount} files" + (excludedCount > 0 ? $" · {excludedCount} excluded" : "");
        ContextLabel.ToolTip = conversation.ProjectPath is null
            ? null
            : conversation.ProjectPath + (conversation.ContextFiles.Count == 0 ? "\nUsing bounded source excerpts" : "\n" + string.Join("\n", conversation.ContextFiles));
        UpdateContextBudgetLabel(conversation);
    }

    private void UpdateContextBudgetLabel(Conversation conversation)
    {
        if (string.IsNullOrWhiteSpace(conversation.ProjectPath) || !Directory.Exists(conversation.ProjectPath))
        {
            ContextEstimateLabel.Text = "";
            ContextEstimateLabel.ToolTip = null;
            return;
        }

        try
        {
            var project = EnsureProject(conversation.ProjectPath);
            var estimate = new WorkspaceFileService(conversation.ProjectPath, project.ContextExclusions).EstimateContextTokens(conversation.ContextFiles);
            var limit = conversation.NumCtx > 0 ? conversation.NumCtx : MaxContextForModel(conversation.Model);
            ContextEstimateLabel.Text = $"≈{FormatTokenCount(estimate)} src tok / {FormatContextLimit(limit)}";
            ContextEstimateLabel.ToolTip = "Approximate tokens in the bounded project source excerpts only. This excludes chat history and instructions; Ollama's reported prompt count above includes the full request.";
        }
        catch (Exception ex)
        {
            ContextEstimateLabel.Text = "";
            ContextEstimateLabel.ToolTip = "Could not estimate project context: " + ex.Message;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var editor = new Window
        {
            Title = "Codev settings", Width = 540, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = ThemeBrush("MainSurfaceBrush"), Foreground = ThemeBrush("MainTextBrush"),
            ResizeMode = ResizeMode.NoResize
        };
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var help = new TextBlock
        {
            Text = $"Appearance: {(_isDarkTheme ? "Dark" : "Light")} · inference: local Ollama at http://127.0.0.1:11434. Chat size is stored on this device.",
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 15)
        };
        layout.Children.Add(help);
        var sizeRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition()); sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sizeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sizeRow.Children.Add(new TextBlock { Text = "Chat text size", FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("MainTextBrush") });
        var sizeLabel = new TextBlock { Text = $"{_chatFontSize:0} pt", Foreground = ThemeBrush("MutedTextBrush"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(sizeLabel, 1); sizeRow.Children.Add(sizeLabel);
        var slider = new Slider { Minimum = 12, Maximum = 22, Value = _chatFontSize, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 0), Foreground = ThemeBrush("WelcomeAccentBrush"), Focusable = true };
        Grid.SetRow(slider, 1); Grid.SetColumnSpan(slider, 2); sizeRow.Children.Add(slider);
        Grid.SetRow(sizeRow, 1); layout.Children.Add(sizeRow);
        var preview = new TextBlock { Text = "The quick brown fox jumps over the lazy dog.", FontSize = _chatFontSize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush("MainTextBrush"), Margin = new Thickness(0, 4, 0, 14) };
        slider.ValueChanged += (_, _) => { sizeLabel.Text = $"{slider.Value:0} pt"; preview.FontSize = slider.Value; };
        Grid.SetRow(preview, 2); layout.Children.Add(preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var apply = new Button { Content = "Apply", Style = (Style)FindResource("SoftButton"), Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        apply.Click += (_, _) => { _chatFontSize = slider.Value; ApplyChatTextSize(); SaveThemePreference(); RenderMessages(); editor.DialogResult = true; editor.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        Grid.SetRow(buttons, 3); layout.Children.Add(buttons);
        editor.Content = layout;
        editor.ShowDialog();
    }

    private void ApplyChatTextSize() => PromptBox.FontSize = Math.Clamp(_chatFontSize, 12, 22);

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme();
        SaveThemePreference();
        RefreshConversationLists();
        RenderMessages();
    }

    private Brush ThemeBrush(string key) => (Brush)FindResource(key);

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _requestCancellation?.Cancel();
        while (_requestQueue.TryDequeuePending(out var turn))
        {
            turn.Conversation.PendingRequestCount = Math.Max(0, turn.Conversation.PendingRequestCount - 1);
            if (turn.AssistantIndex < turn.Conversation.Messages.Count)
                turn.Conversation.Messages[turn.AssistantIndex] = new ChatMessage("assistant", "Queued request was not sent before Codev closed.");
        }
        foreach (var conversation in _conversations)
            for (var i = 0; i < conversation.Messages.Count; i++)
                if (conversation.Messages[i].Role == "assistant" && string.IsNullOrWhiteSpace(conversation.Messages[i].Content))
                    conversation.Messages[i] = new ChatMessage("assistant", "This request did not finish before Codev closed.");
        _ = SaveAsync();
        base.OnClosed(e);
    }

    private sealed record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tool_calls")] List<JsonElement>? ToolCalls = null,
        [property: JsonPropertyName("tool_name")] string? ToolName = null);
    private sealed record OllamaOptions([property: JsonPropertyName("num_ctx")] int NumCtx);
    private sealed record QueuedTurn(Conversation Conversation, int AssistantIndex, string Model, int NumCtx,
        bool IsCodeTask, bool IsPlanMode, string? ProjectPath, List<string> ContextFiles, List<string> ContextExclusions);
    private sealed record UiSettings(string Theme, double? ChatFontSize = null);
    private sealed class TagsResponse { [JsonPropertyName("models")] public List<TagModel>? Models { get; set; } }
    private sealed class TagModel { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    private sealed record ModelOption(string Name, string DisplayName);
    private sealed record ContextOption(int Value, string DisplayName);
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Model { get; set; } = "devstral-small-2-64k";
    public int NumCtx { get; set; }
    public int LastPromptTokens { get; set; }
    public int LastPromptContext { get; set; }
    public string LastPromptModel { get; set; } = "";
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? ProjectPath { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
    public List<FileChangeRecord> FileChanges { get; set; } = [];
    public List<string> ContextFiles { get; set; } = [];
    [JsonIgnore] public int PendingRequestCount { get; set; }
}

public sealed record FileChangeRecord(string RelativePath, string? CheckpointPath, DateTimeOffset ChangedAt, string Kind, bool PreviousFileExisted = true);

public sealed record ChatMessage(string Role, string Content);

public sealed class WorkspaceProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Project";
    public string Path { get; set; } = "";
    public bool IsPinned { get; set; }
    public string Instructions { get; set; } = "";
    public List<string> ContextExclusions { get; set; } = [];
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.Now;
}

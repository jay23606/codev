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
    private readonly ObservableCollection<WorkspaceProject> _projects = [];
    private readonly List<ModelOption> _models = [];
    private Conversation? _active;
    private WorkspaceProject? _activeProject;
    private string? _projectPath;
    private CancellationTokenSource? _requestCancellation;
    private bool _loadingModel;
    private bool _codeTaskMode;
    private bool _planMode;
    private bool _showArchived;
    private Guid? _codeTaskConversationId;
    private bool _isDarkTheme = true;

    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "conversations.json");
    private static string ThemePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "settings.json");
    private static string ProjectsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "projects.json");

    public MainWindow()
    {
        InitializeComponent();
        LoadThemePreference();
        ApplyTheme();
        LoadProjects();
        LoadConversations();
        foreach (var path in _conversations.Select(c => c.ProjectPath).Where(p => !string.IsNullOrWhiteSpace(p))) EnsureProject(path!);
        RefreshConversationLists();
        if (_conversations.Count > 0) SelectConversation(_conversations.OrderByDescending(c => c.UpdatedAt).First());
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
            }
        }
        catch { _isDarkTheme = true; }
    }

    private void SaveThemePreference()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)!);
            File.WriteAllText(ThemePath, JsonSerializer.Serialize(new UiSettings(_isDarkTheme ? "dark" : "light"), JsonOptions));
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
                ["ComboSelectedBrush"] = "#48443F", ["ComboSelectedTextBrush"] = "#F4F1EA"
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
                ["ComboSelectedBrush"] = "#F4E1D8", ["ComboSelectedTextBrush"] = "#45352F"
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
            foreach (var item in saved) _conversations.Add(item);
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
        _codeTaskMode = false;
        _planMode = false;
        _codeTaskConversationId = null;
        UpdateModeButtons();
        _activeProject = conversation.ProjectPath is null ? null : EnsureProject(conversation.ProjectPath);
        UpdateChangesButton(conversation);
        if (_activeProject is not null) _activeProject.LastOpenedAt = DateTimeOffset.Now;
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
        ContextLabel.ToolTip = _projectPath;
    }

    private void RenderMessages()
    {
        MessagesList.Items.Clear();
        if (_active is null) return;
        foreach (var message in _active.Messages)
        {
            var isUser = message.Role == "user";
            var body = new TextBlock { Text = message.Content, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23, Foreground = ThemeBrush("MessageTextBrush") };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = isUser ? "YOU" : "CODEV", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush(isUser ? "UserLabelBrush" : "AssistantLabelBrush"), Margin = new Thickness(0, 0, 0, 6) });
            content.Children.Add(body);
            var border = new Border { Child = content, Padding = new Thickness(isUser ? 15 : 0, isUser ? 12 : 8, isUser ? 15 : 0, isUser ? 12 : 8), Background = isUser ? ThemeBrush("MessageBubbleBrush") : Brushes.Transparent, CornerRadius = new CornerRadius(12), HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, MaxWidth = 720, Margin = new Thickness(0, 0, 0, 17) };
            MessagesList.Items.Add(border);
        }
        ChatScroll.ScrollToEnd();
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
            menu.Items.Add(pinItem);
            menu.Items.Add(instructionsItem);
            menu.Items.Add(openItem);
            button.ContextMenu = menu;
        }
        ProjectsList.Items.Add(button);
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

    private void FillConversationList(ItemsControl list, IEnumerable<Conversation> conversations)
    {
        list.Items.Clear();
        foreach (var item in conversations)
        {
            var title = string.IsNullOrWhiteSpace(item.Title) ? "New conversation" : item.Title;
            var button = new Button { Style = (Style)FindResource("SidebarButton"), Tag = item, Padding = new Thickness(11, 8, 7, 8), Margin = new Thickness(0, 1, 0, 1), Background = ReferenceEquals(item, _active) ? ThemeBrush("SidebarActiveBrush") : Brushes.Transparent };
            var row = new DockPanel();
            var caption = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 188, FontSize = 12, Foreground = ThemeBrush("SidebarTextBrush"), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(caption);
            button.Content = row;
            button.Click += (_, _) => SelectConversation(item);
            var menu = new ContextMenu();
            var pin = new MenuItem { Header = item.IsPinned ? "Unpin conversation" : "Pin conversation" };
            pin.Click += (_, _) => { item.IsPinned = !item.IsPinned; RefreshConversationLists(); _ = SaveAsync(); };
            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) => await RenameConversationAsync(item);
            var archive = new MenuItem { Header = item.IsArchived ? "Restore to conversations" : "Archive conversation" };
            archive.Click += async (_, _) => await SetConversationArchivedAsync(item, !item.IsArchived);
            menu.Items.Add(pin);
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            menu.Items.Add(archive);
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
        if (_requestCancellation is not null)
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
        if (string.IsNullOrWhiteSpace(text) || _active is null || _requestCancellation is not null) return;
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
        var requestCancellation = new CancellationTokenSource();
        _requestCancellation = requestCancellation;
        SendButton.Content = "■";
        SendButton.IsEnabled = true;
        SendButton.ToolTip = "Stop generation";
        AgentStatusLabel.Text = _codeTaskMode ? "Code task · Thinking…" : _planMode ? "Planning locally…" : "Generating locally…";
        await SaveAsync();

        try
        {
            var history = conversation.Messages.Take(conversation.Messages.Count - 1).Select(m => new OllamaMessage(m.Role, m.Content)).ToList();
            var isCodeTask = _codeTaskMode && _codeTaskConversationId == conversation.Id;
            var isPlanMode = _planMode;
            var system = isCodeTask
                ? "You are Codev, a concise local coding agent. Work only within the selected project. Inspect before editing. Use the provided tools instead of claiming actions. Every file replacement and shell command requires user approval. Never represent tool output as successful unless its result confirms success."
                : isPlanMode
                    ? "You are Codev in read-only Plan mode. Give a concise, ordered implementation plan with key files, risks, and checks. Do not edit files, run commands, or claim that any work has been done. Ask a short clarifying question only if a missing detail blocks a useful plan."
                    : "You are Codev, a practical coding assistant. Be concise, explain decisions plainly, and focus on useful implementation details. The user is chatting through a local desktop app. Do not claim you changed files or ran commands; this mode is read-only.";
            if (_activeProject is not null && !string.IsNullOrWhiteSpace(_activeProject.Instructions))
                system += "\n\nProject-specific instructions (apply within this workspace):\n" + _activeProject.Instructions;
            if (!string.IsNullOrWhiteSpace(conversation.ProjectPath) && !isCodeTask)
            {
                system += "\n\nThe user attached this local project folder: " + conversation.ProjectPath + ". Here are selected source files from that folder, included as read-only context. Do not claim to have changed them.";
                system += "\n\n" + await CollectProjectContextAsync(conversation.ProjectPath, requestCancellation.Token);
            }
            history.Insert(0, new OllamaMessage("system", system));
            if (isCodeTask)
            {
                var service = new WorkspaceFileService(conversation.ProjectPath!);
                await RunAgentTurnAsync(conversation, history, service, requestCancellation.Token);
            }
            else
                await RunChatTurnAsync(conversation, history, requestCancellation.Token);
            if (conversation.Messages[^1].Content.Length == 0)
                conversation.Messages[^1] = new ChatMessage("assistant", "The model returned an empty response. Check that the selected model is installed and running in Ollama.");
        }
        catch (OperationCanceledException)
        {
            var partial = conversation.Messages[^1].Content;
            conversation.Messages[^1] = new ChatMessage("assistant", string.IsNullOrWhiteSpace(partial) ? "Generation stopped." : partial + "\n\n[Generation stopped.]");
        }
        catch (Exception ex) { conversation.Messages[^1] = new ChatMessage("assistant", $"Could not complete the request.\n\n{ex.Message}\n\nCheck that Ollama is running and that this model is installed."); }
        finally
        {
            conversation.UpdatedAt = DateTimeOffset.Now;
            if (ReferenceEquals(_active, conversation)) RenderMessages();
            RefreshConversationLists();
            await SaveAsync();
            SendButton.IsEnabled = true;
            SendButton.Content = "↑";
            SendButton.ToolTip = "Send message (Enter)";
            AgentStatusLabel.Text = "Your conversations and model requests stay on this device.";
            _requestCancellation?.Dispose();
            _requestCancellation = null;
        }
    }

    private async Task RunChatTurnAsync(Conversation conversation, List<OllamaMessage> history, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(new { model = conversation.Model, messages = history, stream = true }) };
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
            if (json.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var chunk))
            {
                output.Append(chunk.GetString());
                conversation.Messages[^1] = new ChatMessage("assistant", output.ToString());
                if (ReferenceEquals(_active, conversation)) RenderMessages();
            }
        }
    }

    private async Task RunAgentTurnAsync(Conversation conversation, List<OllamaMessage> history, WorkspaceFileService service, CancellationToken cancellationToken)
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
                Content = JsonContent.Create(new { model = conversation.Model, messages = history, tools, stream = false })
            };
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            var message = json.RootElement.GetProperty("message");
            var text = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? "" : "";
            var calls = message.TryGetProperty("tool_calls", out var callsElement) && callsElement.ValueKind == JsonValueKind.Array
                ? callsElement.EnumerateArray().ToList() : [];
            if (calls.Count == 0)
            {
                conversation.Messages[^1] = new ChatMessage("assistant", text);
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
                AgentStatusLabel.Text = $"Code task · {name.Replace('_', ' ')}";
                var arguments = function.TryGetProperty("arguments", out var args) ? args : default;
                var result = await ExecuteAgentToolAsync(name, arguments, service, conversation, cancellationToken);
                history.Add(new OllamaMessage("tool", result, null, name));
                assistantText.Append("\n\n").Append("Tool ").Append(name).Append(": ").Append(result.Length > 1400 ? result[..1400] + "… [truncated in transcript]" : result);
            }
            AgentStatusLabel.Text = "Code task · Thinking…";
            conversation.Messages[^1] = new ChatMessage("assistant", assistantText.ToString());
            RenderAgentTranscript(conversation);
        }
        throw new InvalidOperationException("The agent reached the eight-step tool limit. Send a follow-up to continue.");
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
        var note = new TextBlock { Text = isNewFile ? "No file exists at this path. Approving creates it in the selected project folder." : "Review both versions. Approving applies the reviewed state; a local checkpoint is saved first when a file exists.", TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetColumnSpan(note, 2); layout.Children.Add(note);
        TextBox ReviewBox(string value) => new() { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = ThemeBrush("InputTextBrush"), Background = ThemeBrush("ComposerBrush"), BorderBrush = ThemeBrush("ComposerBorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(8) };
        var oldBox = ReviewBox(before); var newBox = ReviewBox(after);
        Grid.SetRow(oldBox, 1); Grid.SetColumn(oldBox, 0); Grid.SetRow(newBox, 1); Grid.SetColumn(newBox, 1); layout.Children.Add(oldBox); layout.Children.Add(newBox);
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

    private async Task<string> CollectProjectContextAsync(string root, CancellationToken cancellationToken)
    {
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".csproj", ".sln", ".md", ".json", ".js", ".jsx", ".ts", ".tsx", ".py", ".html", ".css", ".sql", ".xml", ".yml", ".yaml", ".toml", ".props", ".targets" };
        var fileService = new WorkspaceFileService(root);
        var output = new StringBuilder("Selected project files (limited read-only excerpts):\n");
        var count = 0;
        const int maxFiles = 24;
        const int maxChars = 32000;
        const int maxFileChars = 2400;
        try
        {
            foreach (var relative in fileService.ListFiles(maxEntries: 300))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!allowedExtensions.Contains(Path.GetExtension(relative))) continue;
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
        _activeProject = EnsureProject(_projectPath);
        if (_active is not null) { _active.ProjectPath = _projectPath; _ = SaveAsync(); }
        ContextLabel.Text = Path.GetFileName(_projectPath);
        ContextLabel.ToolTip = _projectPath;
        RefreshConversationLists();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, $"Appearance: {(_isDarkTheme ? "Dark" : "Light")} (use the appearance button above Settings to switch).\n\nCodev connects to Ollama at http://127.0.0.1:11434. Conversations and preferences are stored on this device in %LOCALAPPDATA%\\Codev.", "Codev settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

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
        _requestCancellation?.Cancel();
        _ = SaveAsync();
        base.OnClosed(e);
    }

    private sealed record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tool_calls")] List<JsonElement>? ToolCalls = null,
        [property: JsonPropertyName("tool_name")] string? ToolName = null);
    private sealed record UiSettings(string Theme);
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
    public bool IsArchived { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? ProjectPath { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
    public List<FileChangeRecord> FileChanges { get; set; } = [];
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
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.Now;
}

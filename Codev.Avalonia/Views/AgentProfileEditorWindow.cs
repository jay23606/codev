using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Codev.Avalonia.ViewModels;

namespace Codev.Avalonia.Views;

public sealed class AgentProfileEditorWindow : Window
{
    private IReadOnlyList<Codev.AgentProfileDocument> _documents;

    public AgentProfileEditorWindow(IUserAgentProfileEditorService profileService,
        IReadOnlyList<Codev.AgentProfileDocument> documents, Func<Task>? openFolderAsync = null)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));

        var fileNames = new AvaloniaList<string>(_documents.Select(document => document.FileName));
        var selection = new ComboBox
        {
            Name = "AgentProfileSelectionComboBox", ItemsSource = fileNames,
            MinWidth = 240, PlaceholderText = "Select a user profile"
        };
        var fileName = new TextBox
        {
            Name = "AgentProfileFileNameTextBox", Watermark = "profile-name (lowercase, hyphenated)",
            MinWidth = 260, MaxLength = 44
        };
        var editor = new TextBox
        {
            Name = "AgentProfileContentsTextBox", AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code"), FontSize = 12,
            MinHeight = 360, MaxLength = Codev.AgentProfileCatalog.MaxProfileFileBytes,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            Foreground = new SolidColorBrush(Color.Parse("#F2F2F2")),
            Background = new SolidColorBrush(Color.Parse("#171717"))
        };
        var status = new TextBlock
        {
            Name = "AgentProfileEditorStatus", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#B6B6B6"))
        };
        var dirty = false;
        var loading = false;
        string? currentFile = null;

        void LoadDocument(string? name)
        {
            loading = true;
            currentFile = name;
            fileName.Text = name is null ? "my-agent" : Path.GetFileNameWithoutExtension(name);
            editor.Text = name is null
                ? "---\nname: My Agent\ndescription: Describe what this agent is good at.\ndefault_permission: ask\n---\nWrite clear instructions for this agent here.\n"
                : _documents.FirstOrDefault(document => document.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Contents ?? "";
            selection.SelectedItem = name;
            dirty = false;
            loading = false;
            status.Text = name is null ? "New profile · save to add it to your user profiles." : "Changes are saved locally in your user profile folder.";
        }

        selection.SelectionChanged += (_, _) =>
        {
            var selected = selection.SelectedItem as string;
            if (dirty)
            {
                loading = true;
                selection.SelectedItem = currentFile;
                loading = false;
                status.Text = "Save or revert the current profile before switching profiles.";
                return;
            }
            if (!loading && selected is not null && !selected.Equals(currentFile, StringComparison.OrdinalIgnoreCase)) LoadDocument(selected);
        };
        editor.TextChanged += (_, _) =>
        {
            if (!loading)
            {
                dirty = true;
                status.Text = "Unsaved changes";
            }
        };
        fileName.TextChanged += (_, _) =>
        {
            if (!loading)
            {
                dirty = true;
                status.Text = "Unsaved changes";
            }
        };
        if (_documents.Count > 0) LoadDocument(_documents[0].FileName);
        else LoadDocument(null);

        var create = new Button { Name = "NewProfileButton", Content = "New", Classes = { "soft" } };
        var revert = new Button { Name = "RevertProfileButton", Content = "Revert", Classes = { "soft" } };
        var openFolder = new Button { Name = "OpenAgentProfilesFolderButton", Content = "Open folder", Classes = { "soft" } };
        var close = new Button { Content = "Close", Classes = { "soft" } };
        var save = new Button { Name = "SaveAgentProfileButton", Content = "Save profile", Classes = { "soft" } };
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { openFolder, close, save }
        };

        Title = "Manage agent profiles";
        Width = 820;
        Height = 700;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Reusable user profiles", FontSize = 15, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Profiles are Markdown instructions and tool policies. They are validated before saving and never execute scripts. This editor changes user profiles only; trusted project profiles stay in .codev/agents.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { selection, create, revert } },
                new TextBlock { Text = "File name", FontSize = 11 },
                fileName,
                new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                status, buttons
            }
        };

        create.Click += (_, _) =>
        {
            if (dirty) { status.Text = "Save or revert the current profile before creating another."; return; }
            LoadDocument(null);
        };
        revert.Click += (_, _) => LoadDocument(currentFile);
        openFolder.Click += async (_, _) =>
        {
            if (openFolderAsync is not null) await openFolderAsync();
        };
        close.Click += (_, _) => Close();
        save.Click += async (_, _) =>
        {
            var proposedName = (fileName.Text ?? "").Trim();
            if (!proposedName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) proposedName += ".md";
            try
            {
                await profileService.SaveUserAgentProfileAsync(proposedName, editor.Text ?? "");
                _documents = await profileService.GetUserAgentProfileDocumentsAsync();
                fileNames.Clear();
                foreach (var document in _documents) fileNames.Add(document.FileName);
                LoadDocument(Path.GetFileNameWithoutExtension(proposedName) + ".md");
                status.Text = "Profile saved and available in the Code task profile picker.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                status.Text = $"Profile was not saved ({ex.GetType().Name}): {ex.Message}";
            }
        };
    }
}

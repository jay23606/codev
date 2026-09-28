using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Codev;

namespace Codev.Avalonia.Views;

public partial class ProjectFileBrowserWindow : Window
{
    private const int MaxPreviewCharacters = 24_000;
    private readonly WorkspaceFileService _files;
    private readonly string[] _allFiles;
    private readonly HashSet<string> _selectedFiles;

    public ProjectFileBrowserWindow()
    {
        InitializeComponent();
        _files = new WorkspaceFileService(Path.GetTempPath());
        _allFiles = [];
        _selectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileListBox.ItemsSource = _allFiles;
        FileCountText.Text = "0 supported files";
    }

    public ProjectFileBrowserWindow(string projectPath, IEnumerable<string> selectedFiles)
    {
        InitializeComponent();
        _files = new WorkspaceFileService(projectPath);
        _allFiles = _files.ListContextFiles(maxEntries: 500).ToArray();
        _selectedFiles = selectedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProjectPathText.Text = projectPath;
        FileCountText.Text = $"{_allFiles.Length} supported files · 500-file limit";
        FileListBox.ItemsSource = _allFiles;
        StatusText.Text = "Previews stay on this device. Selected files enter prompts according to provider and context settings.";
        if (_allFiles.Length > 0) FileListBox.SelectedIndex = 0;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Filter_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var filter = FilterTextBox.Text?.Trim() ?? "";
        var priorSelection = FileListBox.SelectedItem as string;
        var filtered = _allFiles.Where(path => path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        FileListBox.ItemsSource = filtered;
        FileListBox.SelectedItem = priorSelection is not null && filtered.Contains(priorSelection, StringComparer.OrdinalIgnoreCase)
            ? priorSelection
            : filtered.FirstOrDefault();
        FileCountText.Text = filter.Length == 0
            ? $"{_allFiles.Length} supported files · 500-file limit"
            : $"{filtered.Length} matching files";
    }

    private async void File_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FileListBox.SelectedItem is not string relativePath)
        {
            PreviewTitle.Text = "Select a file to preview";
            PreviewTextBox.Text = "";
            AddButton.IsEnabled = false;
            return;
        }

        var wasSelected = _selectedFiles.Contains(relativePath);
        AddButton.IsEnabled = !wasSelected;
        PreviewTitle.Text = relativePath + (wasSelected ? " · already in context" : "");
        PreviewTextBox.Text = "Loading preview…";
        try
        {
            var content = await _files.ReadFileAsync(relativePath);
            if (!string.Equals(FileListBox.SelectedItem as string, relativePath, StringComparison.Ordinal)) return;
            PreviewTextBox.Text = content.Length > MaxPreviewCharacters
                ? content[..MaxPreviewCharacters] + "\n\n… preview truncated; the source file was not changed."
                : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!string.Equals(FileListBox.SelectedItem as string, relativePath, StringComparison.Ordinal)) return;
            PreviewTextBox.Text = $"Preview unavailable: {ex.Message}";
        }
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (FileListBox.SelectedItem is string relativePath && !_selectedFiles.Contains(relativePath)) Close(relativePath);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(null);
}

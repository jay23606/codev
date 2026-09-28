using System.IO;

namespace Codev.Tests;

public sealed class ProjectContextSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-context-drop-tests", Guid.NewGuid().ToString("N"));

    public ProjectContextSelectionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Dropped_files_add_only_supported_in_project_context_files_and_ignore_duplicates()
    {
        var source = Path.Combine(_root, "src", "app.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "class App {}");
        var secret = Path.Combine(_root, ".env");
        var binary = Path.Combine(_root, "asset.dll");
        File.WriteAllText(secret, "API_KEY=secret");
        File.WriteAllText(binary, "not-source");
        var outside = _root + "-outside.cs";
        File.WriteAllText(outside, "class Outside {}");
        var selected = new List<string>();

        try
        {
            var result = ProjectContextSelection.AddFiles(new WorkspaceFileService(_root), selected,
                [source, source, secret, binary, outside]);

            Assert.Equal(1, result.AddedCount);
            Assert.Equal(4, result.IgnoredCount);
            Assert.Equal(Path.Combine("src", "app.cs"), Assert.Single(selected));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Dropped_files_respect_project_context_exclusions()
    {
        var excluded = Path.Combine(_root, "private", "notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(excluded)!);
        File.WriteAllText(excluded, "do not attach");
        var selected = new List<string>();

        var result = ProjectContextSelection.AddFiles(new WorkspaceFileService(_root, ["private"]), selected, [excluded]);

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.IgnoredCount);
        Assert.Empty(selected);
    }

    [Fact]
    public void File_selection_stops_at_the_same_limit_used_for_prompt_context()
    {
        var paths = Enumerable.Range(0, WorkspaceFileService.MaxContextFiles + 1)
            .Select(index => Path.Combine(_root, $"file-{index:D2}.cs")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "class Sample {}");
        var selected = new List<string>();

        var result = ProjectContextSelection.AddFiles(new WorkspaceFileService(_root), selected, paths);

        Assert.Equal(WorkspaceFileService.MaxContextFiles, result.AddedCount);
        Assert.Equal(1, result.IgnoredCount);
        Assert.Equal(WorkspaceFileService.MaxContextFiles, selected.Count);
    }

    [Fact]
    public void Removing_a_context_file_is_case_insensitive_and_leaves_other_selections_intact()
    {
        var selected = new List<string> { "src/app.cs", "docs/readme.md" };

        Assert.True(ProjectContextSelection.RemoveFile(selected, "SRC/APP.CS"));
        Assert.False(ProjectContextSelection.RemoveFile(selected, "missing.cs"));
        Assert.Equal("docs/readme.md", Assert.Single(selected));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}

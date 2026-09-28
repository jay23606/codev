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
            var result = ProjectContextSelection.AddDroppedFiles(new WorkspaceFileService(_root), selected,
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

        var result = ProjectContextSelection.AddDroppedFiles(new WorkspaceFileService(_root, ["private"]), selected, [excluded]);

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.IgnoredCount);
        Assert.Empty(selected);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}

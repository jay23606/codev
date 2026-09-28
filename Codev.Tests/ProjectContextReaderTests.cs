namespace Codev.Tests;

public sealed class ProjectContextReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-context-reader-tests", Guid.NewGuid().ToString("N"));

    public ProjectContextReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Reads_only_selected_project_files_and_ignores_secrets_and_path_escapes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.cs"), "class App { }\n// selected");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.cs"), "class Other { }");
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "API_KEY=not-for-context");

        var context = await ProjectContextReader.ReadAsync(_root,
            ["src/app.cs", ".env", "../outside.cs"]);

        Assert.Contains("src/app.cs", context);
        Assert.Contains("// selected", context);
        Assert.DoesNotContain("class Other", context);
        Assert.DoesNotContain("not-for-context", context);
        Assert.Contains("limited to 1 source files", context);
    }

    [Fact]
    public async Task Reads_a_bounded_default_file_set_when_none_was_selected()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "project overview");
        await File.WriteAllTextAsync(Path.Combine(_root, "image.png"), "binary placeholder");

        var context = await ProjectContextReader.ReadAsync(_root);

        Assert.Contains("readme.md", context);
        Assert.Contains("project overview", context);
        Assert.DoesNotContain("image.png", context);
    }

    [Fact]
    public async Task Respects_project_exclusions_and_file_excerpt_limit()
    {
        Directory.CreateDirectory(Path.Combine(_root, "private"));
        await File.WriteAllTextAsync(Path.Combine(_root, "private", "notes.md"), "excluded notes");
        await File.WriteAllTextAsync(Path.Combine(_root, "large.txt"), new string('x', WorkspaceFileService.MaxContextFileCharacters + 100));

        var context = await ProjectContextReader.ReadAsync(_root,
            ["private/notes.md", "large.txt"], ["private"]);

        Assert.DoesNotContain("excluded notes", context);
        Assert.Contains("[excerpt truncated]", context);
        Assert.True(context.Length <= WorkspaceFileService.MaxContextCharacters, $"Context length was {context.Length} characters.");
    }

    [Fact]
    public async Task Total_context_stays_within_the_shared_character_budget()
    {
        var selected = new List<string>();
        for (var i = 0; i < WorkspaceFileService.MaxContextFiles; i++)
        {
            var name = $"file-{i:D2}.txt";
            await File.WriteAllTextAsync(Path.Combine(_root, name), new string('x', WorkspaceFileService.MaxContextFileCharacters));
            selected.Add(name);
        }

        var context = await ProjectContextReader.ReadAsync(_root, selected);

        Assert.True(context.Length <= WorkspaceFileService.MaxContextCharacters, $"Context length was {context.Length} characters.");
        Assert.Contains("[excerpt truncated]", context);
    }

    [Fact]
    public async Task Honors_cancellation_before_reading_files()
    {
        var source = Path.Combine(_root, "app.cs");
        await File.WriteAllTextAsync(source, "class App {}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectContextReader.ReadAsync(_root, ["app.cs"], cancellationToken: cancellation.Token));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}

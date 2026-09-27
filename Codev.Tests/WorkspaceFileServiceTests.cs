using System.IO;

namespace Codev.Tests;

public sealed class WorkspaceFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-tests", Guid.NewGuid().ToString("N"));
    private WorkspaceFileService Service => new(_root);

    public WorkspaceFileServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Resolves_relative_paths_inside_the_workspace()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var actual = Service.ResolvePath(Path.Combine("src", "app.cs"));
        Assert.Equal(Path.Combine(_root, "src", "app.cs"), actual);
    }

    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("sub\\..\\..\\outside.txt")]
    public void Rejects_paths_that_escape_the_workspace(string relativePath)
    {
        Assert.Throws<UnauthorizedAccessException>(() => Service.ResolvePath(relativePath));
    }

    [Fact]
    public void Rejects_absolute_paths_outside_the_workspace()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-codev-test.txt");
        Assert.Throws<UnauthorizedAccessException>(() => Service.ResolvePath(outside));
    }

    [Fact]
    public void Root_boundary_comparison_does_not_accept_sibling_prefixes()
    {
        Assert.True(WorkspaceFileService.IsPathWithinRoot(_root, Path.Combine(_root, "src", "a.cs")));
        Assert.False(WorkspaceFileService.IsPathWithinRoot(_root, _root + "-sibling\\secret.txt"));
    }

    [Fact]
    public async Task Reads_files_and_creates_a_recoverable_checkpoint_before_replacement()
    {
        var relative = "Program.cs";
        await File.WriteAllTextAsync(Path.Combine(_root, relative), "old version");
        var service = Service;

        Assert.Equal("old version", await service.ReadFileAsync(relative));
        var checkpoint = await service.CreateCheckpointAsync(relative, Guid.NewGuid());
        await service.WriteFileAtomicAsync(relative, "new version");

        Assert.NotNull(checkpoint);
        Assert.Equal("old version", await File.ReadAllTextAsync(checkpoint!));
        Assert.Equal("new version", await File.ReadAllTextAsync(Path.Combine(_root, relative)));
    }

    [Fact]
    public void Hides_secret_files_from_both_path_resolution_and_project_listing()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=do-not-read");
        File.WriteAllText(Path.Combine(_root, "app.cs"), "class App {}");
        var service = Service;

        Assert.Throws<UnauthorizedAccessException>(() => service.ResolvePath(".env"));
        Assert.Contains("app.cs", service.ListFiles());
        Assert.DoesNotContain(".env", service.ListFiles());
    }

    [Fact]
    public void Blocks_files_under_sensitive_directories_and_symbolic_links()
    {
        var secretDirectory = Path.Combine(_root, "secrets");
        Directory.CreateDirectory(secretDirectory);
        File.WriteAllText(Path.Combine(secretDirectory, "notes.txt"), "private");
        Assert.Throws<UnauthorizedAccessException>(() => Service.ResolvePath("secrets/notes.txt"));
        Assert.DoesNotContain("secrets/notes.txt", Service.ListFiles());

        var outside = Path.Combine(Path.GetTempPath(), "outside-codev-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.cs"), "private");
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside); }
            catch (IOException) { return; }
            Assert.Throws<UnauthorizedAccessException>(() => Service.ResolvePath("linked/outside.cs"));
            Assert.DoesNotContain("linked/outside.cs", Service.ListFiles());
        }
        finally
        {
            try { if (Directory.Exists(Path.Combine(_root, "linked"))) Directory.Delete(Path.Combine(_root, "linked")); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

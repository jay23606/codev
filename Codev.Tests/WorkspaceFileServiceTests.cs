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
    public async Task Refuses_to_overwrite_a_file_changed_after_its_review_snapshot()
    {
        var path = Path.Combine(_root, "Program.cs");
        await File.WriteAllTextAsync(path, "version before review");
        var service = Service;
        var snapshot = await service.ReadFileSnapshotAsync("Program.cs");
        await File.WriteAllTextAsync(path, "newer user edit");

        await Assert.ThrowsAsync<IOException>(() => service.CreateCheckpointAsync("Program.cs", Guid.NewGuid(), expectedHash: snapshot.Sha256));
        await Assert.ThrowsAsync<IOException>(() => service.WriteFileAtomicAsync("Program.cs", "agent edit", expectedOriginalHash: snapshot.Sha256));
        Assert.Equal("newer user edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Restores_a_conversation_checkpoint_and_keeps_a_rollback_of_the_replaced_version()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_root, "Program.cs");
        var checkpoints = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", id.ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "before edit");
            var service = Service;
            var checkpoint = await service.CreateCheckpointAsync("Program.cs", id);
            await service.WriteFileAtomicAsync("Program.cs", "after edit");
            var current = await service.ReadFileSnapshotAsync("Program.cs");

            var rollback = await service.RestoreCheckpointAsync("Program.cs", id, checkpoint!, current.Sha256);

            Assert.Equal("before edit", await File.ReadAllTextAsync(path));
            Assert.Equal("after edit", await File.ReadAllTextAsync(rollback));
        }
        finally { try { if (Directory.Exists(checkpoints)) Directory.Delete(checkpoints, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Does_not_read_a_checkpoint_outside_the_conversation_backup_folder()
    {
        var outside = Path.Combine(_root, "outside.bak");
        await File.WriteAllTextAsync(outside, "not a checkpoint");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.ReadCheckpointAsync("Program.cs", Guid.NewGuid(), outside));
    }

    [Fact]
    public async Task Runs_an_explicitly_approved_command_from_workspace_and_captures_output()
    {
        var result = await Service.RunApprovedCommandAsync("Write-Output 'Codev smoke test'; exit 7", TimeSpan.FromSeconds(20));
        Assert.Contains("Codev smoke test", result);
        Assert.Contains("Exit code: 7", result);
    }

    [Fact]
    public async Task Terminates_a_command_when_its_timeout_expires()
    {
        var result = await Service.RunApprovedCommandAsync("Start-Sleep -Seconds 10", TimeSpan.FromMilliseconds(150));
        Assert.Contains("timed out", result);
    }

    [Fact]
    public async Task Bounds_captured_command_output()
    {
        var result = await Service.RunApprovedCommandAsync("1..5000 | ForEach-Object { '0123456789' }", TimeSpan.FromSeconds(20));
        Assert.True(result.Length < 22_000);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Hides_secret_and_binary_files_from_agent_access_and_project_listing()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=do-not-read");
        File.WriteAllText(Path.Combine(_root, "app.cs"), "class App {}");
        File.WriteAllBytes(Path.Combine(_root, "diagram.png"), [0, 1, 2, 3]);
        var service = Service;

        Assert.Throws<UnauthorizedAccessException>(() => service.ResolvePath(".env"));
        Assert.Contains("app.cs", service.ListFiles());
        Assert.DoesNotContain(".env", service.ListFiles());
        Assert.DoesNotContain("diagram.png", service.ListFiles());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadFileAsync("diagram.png"));
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

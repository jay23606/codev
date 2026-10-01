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
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
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
    public void Rejects_windows_drive_paths_even_on_unix()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Service.ResolvePath("C:\\outside\\secret.txt"));
    }

    [Fact]
    public void Root_boundary_comparison_does_not_accept_sibling_prefixes()
    {
        Assert.True(WorkspaceFileService.IsPathWithinRoot(_root, Path.Combine(_root, "src", "a.cs")));
        Assert.False(WorkspaceFileService.IsPathWithinRoot(_root, _root + "-sibling\\secret.txt"));
        if (!OperatingSystem.IsWindows())
            Assert.False(WorkspaceFileService.IsPathWithinRoot(_root, _root.ToUpperInvariant() + "/secret.txt"));
    }

    [Fact]
    public void Context_estimate_counts_only_the_selected_bounded_source_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), new string('a', 40));
        File.WriteAllText(Path.Combine(_root, "src", "b.cs"), new string('b', 120));

        var estimate = Service.EstimateContextTokens([Path.Combine("src", "a.cs")]);

        Assert.Equal(16, estimate); // (40 file bytes + 8 path characters + 16 framing characters) / 4.
    }

    [Fact]
    public void Context_estimate_excludes_secret_files_even_when_selected()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "API_KEY=secret-value");

        Assert.Equal(0, Service.EstimateContextTokens([".env"]));
    }

    [Fact]
    public void Project_context_exclusions_filter_context_without_limiting_agent_file_access()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "generated"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "src", "generated", "api.cs"), "generated source");
        File.WriteAllText(Path.Combine(_root, "src", "main.cs"), "main source");
        File.WriteAllText(Path.Combine(_root, "docs", "site.min.js"), "minified source");
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "helpful context");
        var service = new WorkspaceFileService(_root, ["generated", "*.min.js"]);

        var allAgentFiles = service.ListFiles(maxEntries: 100);
        var contextFiles = service.ListContextFiles(maxEntries: 100);

        Assert.Contains(Path.Combine("src", "generated", "api.cs"), allAgentFiles);
        Assert.DoesNotContain(Path.Combine("src", "generated", "api.cs"), contextFiles);
        Assert.Equal(0, service.EstimateContextTokens([Path.Combine("src", "generated", "api.cs")]));
        Assert.DoesNotContain(Path.Combine("docs", "site.min.js"), contextFiles);
        Assert.Contains(Path.Combine("src", "main.cs"), contextFiles);
        Assert.Contains(Path.Combine("docs", "guide.md"), contextFiles);
    }

    [Fact]
    public void Context_exclusion_rules_reject_absolute_traversal_and_path_globs()
    {
        Assert.True(WorkspaceFileService.IsValidContextExclusion("src/generated"));
        Assert.True(WorkspaceFileService.IsValidContextExclusion("*.min.js"));
        Assert.False(WorkspaceFileService.IsValidContextExclusion("../outside"));
        Assert.False(WorkspaceFileService.IsValidContextExclusion("src/*.cs"));
        Assert.False(WorkspaceFileService.IsValidContextExclusion("C:\\outside"));
    }

    [Theory]
    [InlineData("Main.java")]
    [InlineData("server.go")]
    [InlineData("lib.rs")]
    [InlineData("native.cpp")]
    [InlineData("Screen.kt")]
    [InlineData("View.swift")]
    [InlineData("component.vue")]
    [InlineData("widget.svelte")]
    public async Task Common_source_languages_are_available_to_context_search_and_file_reads(string fileName)
    {
        var relativePath = Path.Combine("src", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, relativePath))!);
        await File.WriteAllTextAsync(Path.Combine(_root, relativePath), "class Sample { } // language-marker");

        var service = Service;
        Assert.Contains(relativePath, service.ListContextFiles());
        Assert.Equal("class Sample { } // language-marker", await service.ReadFileAsync(relativePath));
        Assert.Contains(await service.SearchFilesAsync("language-marker"), match => match.Contains("language-marker", StringComparison.Ordinal));
        await service.WriteFileAtomicAsync(relativePath, "updated source");
        Assert.Equal("updated source", await File.ReadAllTextAsync(Path.Combine(_root, relativePath)));
        var newRelativePath = Path.Combine("src", "new-" + fileName);
        await service.CreateFileAtomicAsync(newRelativePath, "new source");
        Assert.Equal("new source", await service.ReadFileAsync(newRelativePath));
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("program.exe")]
    [InlineData("model.wasm")]
    public async Task Binary_and_archive_files_remain_outside_the_agent_allowlist(string fileName)
    {
        Assert.False(Service.IsSupportedContextFile(fileName));
        await File.WriteAllTextAsync(Path.Combine(_root, fileName), "not a source file");
        Assert.DoesNotContain(fileName, Service.ListFiles());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ReadFileAsync(fileName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.WriteFileAtomicAsync(fileName, "replacement"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateFileAtomicAsync(Path.Combine("new-" + fileName), "new file"));
    }

    [Fact]
    public async Task Chat_content_search_returns_line_matches_and_respects_context_exclusions()
    {
        Directory.CreateDirectory(Path.Combine(_root, "generated"));
        File.WriteAllText(Path.Combine(_root, "app.cs"), "class App\n// startup marker");
        File.WriteAllText(Path.Combine(_root, "generated", "client.cs"), "// startup marker from generated code");
        var service = new WorkspaceFileService(_root, ["generated"]);

        var chatMatches = await service.SearchContextFilesAsync("startup marker");
        var agentMatches = await service.SearchFilesAsync("startup marker");

        var match = Assert.Single(chatMatches);
        Assert.Equal("app.cs", match.RelativePath);
        Assert.Equal(2, match.LineNumber);
        Assert.Equal("// startup marker", match.LineText);
        Assert.Contains(agentMatches, result => result.StartsWith($"generated{Path.DirectorySeparatorChar}client.cs:1:", StringComparison.Ordinal));
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
    public async Task Creates_a_new_file_and_can_undo_and_redo_its_creation()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_root, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var checkpoints = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", id.ToString("N"));
        try
        {
            var service = Service;
            await service.CreateFileAtomicAsync("src/Feature.cs", "class Feature {}");
            var created = await service.ReadFileSnapshotAsync("src/Feature.cs");
            var rollback = await service.RestoreFileStateAsync("src/Feature.cs", id, previousFileExisted: false, checkpointPath: null, expectedCurrentHash: created.Sha256);
            Assert.NotNull(rollback);
            Assert.False(File.Exists(path));

            var redo = await service.RestoreFileStateAsync("src/Feature.cs", id, previousFileExisted: true, checkpointPath: rollback, expectedCurrentHash: null);
            Assert.Null(redo);
            Assert.Equal("class Feature {}", await File.ReadAllTextAsync(path));
        }
        finally { try { if (Directory.Exists(checkpoints)) Directory.Delete(checkpoints, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Restoring_a_checkpoint_preserves_original_bytes_even_when_text_is_invalid_utf8()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_root, "source.txt");
        var originalBytes = new byte[] { 0xFF, 0x00, 0xC3, 0x28, 0x0D, 0x0A };
        var checkpoints = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", id.ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, originalBytes);
            var service = Service;
            var checkpoint = await service.CreateCheckpointAsync("source.txt", id);
            await File.WriteAllTextAsync(path, "replacement");
            var replacement = await service.ReadFileSnapshotAsync("source.txt");

            await service.RestoreFileStateAsync("source.txt", id, previousFileExisted: true, checkpoint, replacement.Sha256);

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        }
        finally { try { if (Directory.Exists(checkpoints)) Directory.Delete(checkpoints, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Refuses_to_create_a_checkpoint_larger_than_the_restore_limit()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "large.txt"), new byte[500_001]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateCheckpointAsync("large.txt", Guid.NewGuid()));
    }

    [Fact]
    public async Task Checkpoint_paths_remain_unique_for_rapid_changes_to_the_same_file()
    {
        var path = Path.Combine(_root, "rapid.js");
        var id = Guid.NewGuid();
        var checkpoints = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", id.ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "first");
            var first = await Service.CreateCheckpointAsync("rapid.js", id);
            await File.WriteAllTextAsync(path, "second");
            var second = await Service.CreateCheckpointAsync("rapid.js", id);

            Assert.NotEqual(first, second);
            Assert.Equal("first", await Service.ReadCheckpointAsync("rapid.js", id, first!));
            Assert.Equal("second", await Service.ReadCheckpointAsync("rapid.js", id, second!));
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

    [Fact]
    public void Refuses_a_symbolic_link_used_as_the_project_root()
    {
        var target = Path.Combine(_root, "real-project");
        var linkedRoot = Path.Combine(_root, "linked-project");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "outside.cs"), "class Outside {}");
        try { Directory.CreateSymbolicLink(linkedRoot, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

        Assert.Throws<UnauthorizedAccessException>(() => new WorkspaceFileService(linkedRoot));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

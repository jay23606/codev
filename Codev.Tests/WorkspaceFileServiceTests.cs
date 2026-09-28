using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

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
        Assert.Contains(agentMatches, result => result.StartsWith("generated\\client.cs:1:", StringComparison.Ordinal));
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
    public async Task Does_not_read_a_checkpoint_outside_the_conversation_backup_folder()
    {
        var outside = Path.Combine(_root, "outside.bak");
        await File.WriteAllTextAsync(outside, "not a checkpoint");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.ReadCheckpointAsync("Program.cs", Guid.NewGuid(), outside));
    }

    [Fact]
    public void Renders_markdown_fences_and_copy_control_as_separate_blocks()
    {
        var hasHeading = false;
        var hasCopy = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rendered = MarkdownRenderer.Render("# Heading\n\n`inline`\n\n```js\nconst x = 1;\n```", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral);
                hasHeading = rendered.Children.OfType<TextBlock>().Any(block => block.Inlines.OfType<Run>().Any(run => run.Text.Contains("Heading", StringComparison.Ordinal)));
                hasCopy = rendered.Children.OfType<Border>().Any(border => FindChild<Button>(border)?.Content?.ToString() == "Copy");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        Assert.True(hasHeading);
        Assert.True(hasCopy);
    }

    [Fact]
    public void Markdown_text_size_scales_body_and_headings_and_is_bounded()
    {
        double headingSize = 0;
        double bodySize = 0;
        double clampedSize = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rendered = MarkdownRenderer.Render("# Heading\n\nBody", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral, baseFontSize: 18);
                headingSize = rendered.Children.OfType<TextBlock>().First(block => block.Inlines.OfType<Run>().Any(run => run.Text.Contains("Heading", StringComparison.Ordinal))).FontSize;
                bodySize = rendered.Children.OfType<TextBlock>().First(block => block.Inlines.OfType<Run>().Any(run => run.Text.Contains("Body", StringComparison.Ordinal))).FontSize;
                var clamped = MarkdownRenderer.Render("Body", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral, baseFontSize: 100);
                clampedSize = clamped.Children.OfType<TextBlock>().Single().FontSize;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;

        Assert.Equal(18, bodySize);
        Assert.Equal(18 * 1.57, headingSize, 4);
        Assert.Equal(22, clampedSize);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
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

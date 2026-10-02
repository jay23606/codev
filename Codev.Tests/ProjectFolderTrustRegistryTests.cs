using Codev;

namespace Codev.Tests;

public sealed class ProjectFolderTrustRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codev-trust-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "trusted-folders.json");

    public ProjectFolderTrustRegistryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Trust_is_persisted_for_folder_and_descendants_and_can_be_revoked()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);

        await registry.TrustAsync(project);

        Assert.True(registry.IsTrusted(project));
        Assert.True(registry.IsTrusted(Path.Combine(project, "src", "nested")));
        var reloaded = ProjectFolderTrustRegistry.Load(SettingsPath);
        Assert.True(reloaded.IsTrusted(project));
        await reloaded.RevokeAsync(project);
        Assert.False(ProjectFolderTrustRegistry.Load(SettingsPath).IsTrusted(project));
    }

    [Fact]
    public async Task Trusted_folder_prefix_does_not_trust_a_sibling()
    {
        var project = Path.Combine(_root, "project");
        var sibling = Path.Combine(_root, "project-copy");
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);
        await registry.TrustAsync(project);

        Assert.False(registry.IsTrusted(sibling));
    }

    [Fact]
    public async Task Child_reports_parent_trust_source_and_cannot_remove_parent_scope_by_revoke()
    {
        var child = Path.Combine(_root, "parent", "child");
        var parent = Path.GetDirectoryName(child)!;
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);
        await registry.TrustAsync(parent);

        Assert.Equal(Path.GetFullPath(parent), registry.FindTrustedRoot(child));
        Assert.False(registry.IsDirectTrustRoot(child));
        await registry.RevokeAsync(child);
        Assert.True(registry.IsTrusted(child));
    }

    [Fact]
    public async Task Keeping_a_folder_untrusted_is_remembered_without_trusting_it()
    {
        var project = Path.Combine(_root, "project");
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);
        await registry.MarkKnownAsync(project);

        var reloaded = ProjectFolderTrustRegistry.Load(SettingsPath);
        Assert.True(reloaded.IsKnown(project));
        Assert.False(reloaded.IsTrusted(project));
    }

    [Fact]
    public async Task Unreadable_trust_file_is_preserved_and_cannot_be_overwritten()
    {
        const string contents = "not valid json";
        File.WriteAllText(SettingsPath, contents);
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);

        Assert.False(registry.CanWrite);
        Assert.False(registry.IsTrusted(_root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TrustAsync(_root));
        Assert.Equal(contents, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task Filesystem_root_cannot_be_trusted()
    {
        var root = Path.GetPathRoot(_root)!;
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TrustAsync(root));
        Assert.False(registry.IsTrusted(_root));
    }

    [Fact]
    public async Task Trust_fails_closed_when_a_trusted_root_becomes_a_symbolic_link()
    {
        var project = Path.Combine(_root, "project");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);
        await registry.TrustAsync(project);

        Directory.Delete(project);
        try { Directory.CreateSymbolicLink(project, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

        Assert.False(registry.IsTrusted(project));
        Assert.Throws<UnauthorizedAccessException>(() => new WorkspaceFileService(project));
    }

    [Fact]
    public async Task Trust_refuses_a_project_path_under_a_symbolic_link_parent()
    {
        var outside = Path.Combine(_root, "outside");
        var linkedParent = Path.Combine(_root, "linked-parent");
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(linkedParent, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        var project = Path.Combine(linkedParent, "project");
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.TrustAsync(project));
        Assert.False(registry.IsTrusted(project));
    }

    [Fact]
    public async Task Concurrent_trust_reads_observe_safe_snapshots_during_updates()
    {
        var registry = ProjectFolderTrustRegistry.Load(SettingsPath);
        var folders = Enumerable.Range(0, 32).Select(index => Path.Combine(_root, $"concurrent-{index}")).ToArray();
        using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(() =>
        {
            start.Wait();
            for (var iteration = 0; iteration < 5_000; iteration++)
            {
                registry.TrustedRoots.ToArray();
                registry.IsKnown(folders[(iteration + readerIndex) % folders.Length]);
                registry.IsTrusted(folders[(iteration + readerIndex) % folders.Length]);
                registry.FindTrustedRoot(folders[(iteration + readerIndex) % folders.Length]);
            }
        })).ToArray();
        var writer = Task.Run(async () =>
        {
            start.Set();
            foreach (var folder in folders)
            {
                await registry.TrustAsync(folder);
                await registry.RevokeAsync(folder);
                await registry.MarkKnownAsync(folder);
            }
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.All(folders, folder => Assert.True(registry.IsKnown(folder)));
        Assert.Empty(registry.TrustedRoots);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

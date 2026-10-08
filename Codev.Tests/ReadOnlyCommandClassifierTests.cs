using Codev;

namespace Codev.Tests;

public sealed class ReadOnlyCommandClassifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-read-only-commands", Guid.NewGuid().ToString("N"));

    public ReadOnlyCommandClassifierTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "project readme");
    }

    [Theory]
    [InlineData("PowerShell", "Get-Location")]
    [InlineData("PowerShell", "pwd")]
    [InlineData("PowerShell", "Get-ChildItem src")]
    [InlineData("bash", "pwd")]
    [InlineData("bash", "ls src")]
    public void Allows_simple_inspection_commands(string shell, string command) =>
        Assert.True(ReadOnlyCommandClassifier.IsReadOnly(command, _root, shell));

    [Fact]
    public async Task File_reads_require_windows_hard_link_verification()
    {
        var shell = OperatingSystem.IsWindows() ? "PowerShell" : "bash";
        var command = OperatingSystem.IsWindows() ? "Get-Content README.md" : "cat README.md";
        Assert.Equal(OperatingSystem.IsWindows(), ReadOnlyCommandClassifier.IsReadOnly(command, _root, shell));
        if (OperatingSystem.IsWindows())
        {
            var output = await ReadOnlyCommandClassifier.ExecuteAsync(command, _root, shell);
            Assert.Contains("project readme", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => StartReadOnlyCommand(command, shell));
        }
    }

    [Theory]
    [InlineData("PowerShell", "Get-Content README.md; Remove-Item README.md")]
    [InlineData("bash", "cat README.md && rm README.md")]
    [InlineData("bash", "cat README.md > out.txt")]
    [InlineData("PowerShell", "Get-Content README.md | Out-File out.txt")]
    [InlineData("bash", "sh -c cat README.md")]
    [InlineData("bash", "cat ../outside.txt")]
    [InlineData("bash", "cat /etc/passwd")]
    [InlineData("bash", "cat *.txt")]
    [InlineData("bash", "cat README.md\nrm README.md")]
    [InlineData("bash", "cat README.md\u2028rm README.md")]
    [InlineData("bash", "cat README.md !history")]
    [InlineData("bash", "cat .env")]
    [InlineData("bash", "cat README.md\\; rm README.md")]
    [InlineData("bash", "git status")]
    [InlineData("PowerShell", "Get-Content README.md -Raw")]
    public void Ambiguous_or_mutating_commands_still_require_approval(string shell, string command) =>
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly(command, _root, shell));

    [Fact]
    public void Missing_files_and_unknown_shells_fail_closed()
    {
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat missing.txt", _root, "bash"));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat README.md", _root, "custom-shell"));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("pwd", Path.Combine(_root, "missing"), "bash"));
    }

    [Fact]
    public void Location_commands_refuse_codev_app_data_roots()
    {
        var localData = CodevDataPaths.LocalDataRoot;
        if (string.IsNullOrWhiteSpace(localData)) return;
        var protectedRoot = Path.Combine(localData, "Codev");
        var protectedRootExisted = Directory.Exists(protectedRoot);
        Directory.CreateDirectory(protectedRoot);
        var project = Path.Combine(protectedRoot, "read-only-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            Assert.False(ReadOnlyCommandClassifier.IsReadOnly("pwd", project, "bash"));
            Assert.False(ReadOnlyCommandClassifier.IsReadOnly("Get-Location", project, "PowerShell"));
        }
        finally
        {
            Directory.Delete(project);
            if (!protectedRootExisted && !Directory.EnumerateFileSystemEntries(protectedRoot).Any()) Directory.Delete(protectedRoot);
        }
    }

    [Fact]
    public void Read_only_commands_respect_secret_names_ignored_folders_and_project_exclusions()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "my-secret-config.json"), "secret");

        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat my-secret-config.json", _root, "bash"));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat package.json", _root, "bash", ["README.md", "package.json"]));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat node_modules", _root, "bash"));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("ls node_modules", _root, "bash"));
    }

    [Fact]
    public async Task Read_only_command_runs_as_bounded_file_inspection_without_a_shell()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_root, ".env"), "secret");
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "visible");
        File.WriteAllText(Path.Combine(_root, "package.json"), "excluded");
        var content = await ReadOnlyCommandClassifier.ExecuteAsync("cat README.md", _root, "bash");
        var listing = await ReadOnlyCommandClassifier.ExecuteAsync("ls", _root, "bash", contextExclusions: ["package.json"]);

        Assert.Contains("project readme", content, StringComparison.Ordinal);
        Assert.Contains("visible.txt", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("package.json", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_file_output_is_capped()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_root, "large.md"), new string('x', 20_000));
        var output = await ReadOnlyCommandClassifier.ExecuteAsync("cat large.md", _root, "bash");

        Assert.True(output.Length < 8100);
        Assert.Contains("read-only inspection output truncated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_mode_refuses_hard_linked_files()
    {
        if (!OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(), "Codev-linked-secret-" + Guid.NewGuid().ToString("N") + ".md");
        var link = Path.Combine(_root, "linked.md");
        File.WriteAllText(outside, "external file contents");
        try
        {
            Assert.True(CreateHardLink(link, outside, IntPtr.Zero));
            Assert.False(ReadOnlyCommandClassifier.IsReadOnly("Get-Content linked.md", _root, "PowerShell"));
            Assert.Throws<InvalidOperationException>(() => StartReadOnlyCommand("Get-Content linked.md", "PowerShell"));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Read_only_mode_auto_approves_only_classified_commands_and_keeps_deny_rules_first()
    {
        var registry = ProjectCommandPermissionRegistry.Load(Path.Combine(_root, "permissions.json"));
        await registry.SetModeAsync(_root, ProjectCommandPermissionMode.ReadOnly);
        var readShell = OperatingSystem.IsWindows() ? "PowerShell" : "bash";
        var readCommand = OperatingSystem.IsWindows() ? "Get-Content README.md" : "cat README.md";
        Assert.Equal(OperatingSystem.IsWindows() ? ProjectCommandPermissionDecision.Allow : ProjectCommandPermissionDecision.Ask,
            registry.Evaluate(_root, readCommand, readShell));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_root, readCommand, readShell, contextExclusions: ["README.md"]));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_root, "cat README.md", "bash", allowReadOnly: false));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_root, "cat README.md; rm README.md", "bash"));
        await registry.SetRuleAsync(_root, "cat README.md", ProjectCommandPermissionDecision.Deny);
        Assert.Equal(ProjectCommandPermissionDecision.Deny, registry.Evaluate(_root, "cat README.md", "bash"));
    }

    [Fact]
    public void Refuses_symlinked_targets()
    {
        var outside = Path.Combine(Path.GetTempPath(), "Codev-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var link = Path.Combine(_root, "linked.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            File.CreateSymbolicLink(link, outside);
            Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat linked.txt", _root, "bash"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Symlink creation is unavailable in some Windows configurations.
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Location_commands_refuse_a_symlinked_project_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "Codev-outside-root-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "Codev-linked-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            Assert.False(ReadOnlyCommandClassifier.IsReadOnly("pwd", link, "bash"));
            Assert.False(ReadOnlyCommandClassifier.IsReadOnly("Get-Location", link, "PowerShell"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Symlink creation is unavailable in some Windows configurations.
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void StartReadOnlyCommand(string command, string shell) =>
        _ = ReadOnlyCommandClassifier.ExecuteAsync(command, _root, shell);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

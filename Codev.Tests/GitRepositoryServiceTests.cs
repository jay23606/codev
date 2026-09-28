using System.Diagnostics;
using System.IO;

namespace Codev.Tests;

public sealed class GitRepositoryServiceTests
{
    [Fact]
    public async Task Status_reports_branch_changes_and_only_switches_existing_branches_when_clean()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var file = Path.Combine(root, "read me.txt");
            await File.WriteAllTextAsync(file, "base\n");
            await RunGitAsync(root, "add", "--", "read me.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            await RunGitAsync(root, "branch", "feature/test");

            var service = new GitRepositoryService(root);
            var clean = await service.GetStatusAsync();
            Assert.Equal(Path.GetFullPath(root), clean.Root);
            Assert.Equal("main", clean.Branch);
            Assert.False(clean.HasChanges);
            Assert.Contains("feature/test", await service.GetLocalBranchesAsync());

            await service.SwitchBranchAsync("feature/test");
            Assert.Equal("feature/test", (await service.GetStatusAsync()).Branch);

            await File.AppendAllTextAsync(file, "modified\n");
            var dirty = await service.GetStatusAsync();
            Assert.True(dirty.HasChanges);
            Assert.Contains(dirty.Files, entry => entry.Path == "read me.txt" && entry.WorkingTree == "M");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchBranchAsync("main"));
            Assert.Equal("feature/test", (await service.GetStatusAsync()).Branch);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Status_explains_when_a_folder_is_not_a_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new GitRepositoryService(root);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetStatusAsync());
            Assert.Contains("not inside a Git repository", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task RunGitAsync(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(root, "missing-global-config");
        process.StartInfo.Environment["GIT_CONFIG_SYSTEM"] = Path.Combine(root, "missing-system-config");
        Assert.True(process.Start());
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {output} {error}");
    }
}

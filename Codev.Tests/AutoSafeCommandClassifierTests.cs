using Codev;

namespace Codev.Tests;

public sealed class AutoSafeCommandClassifierTests
{
    [Theory]
    [InlineData("gh api repos/../project/pages")]
    [InlineData("gh api repos/example/../../pages")]
    [InlineData("gh api repos/./project/pages")]
    public void Rejects_path_traversal_owner_or_repository_segments(string command)
    {
        Assert.False(AutoSafeCommandClassifier.IsSafe(command));
    }

    [Fact]
    public void Scoped_git_checks_allow_existing_project_directories_but_reject_missing_or_linked_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-auto-git-scope", Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.True(AutoSafeCommandClassifier.IsSafe("git -C nested status --short", root));
            Assert.False(AutoSafeCommandClassifier.IsSafe("git -C missing status --short", root));
            Assert.False(AutoSafeCommandClassifier.IsSafe("git -C ../outside status --short", root));

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
                Assert.False(AutoSafeCommandClassifier.IsSafe("git -C linked status --short", root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Symlink creation is optional on Windows accounts without Developer Mode or link privileges.
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Scoped_git_checks_do_not_discover_a_repository_above_the_trusted_project()
    {
        var repository = Path.Combine(Path.GetTempPath(), "Codev-auto-parent-repo", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(repository, "trusted-subfolder");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(project);
        try
        {
            Assert.True(AutoSafeCommandClassifier.IsSafe("git status --short", repository));
            Assert.False(AutoSafeCommandClassifier.IsSafe("git status --short", project));
        }
        finally { try { Directory.Delete(repository, recursive: true); } catch { } }
    }

    [Theory]
    [InlineData("git -C space-invaders-game status --short")]
    [InlineData("git -C space-invaders-game branch --show-current")]
    [InlineData("git -C space-invaders-game log -1 --oneline")]
    [InlineData("gh auth status")]
    [InlineData("gh api repos/jay23606/space-invaders-game/pages")]
    [InlineData("gh api repos/jay23606/space-invaders-game/pages --jq '{url: .html_url, status: .status, source: .source}'")]
    [InlineData("gh api repos/jay23606/space-invaders-game/pages --jq \"{url: .html_url, status: .status, source: .source}\"")]
    [InlineData("Start-Sleep -Seconds 10; gh api repos/jay23606/space-invaders-game/pages --jq '{url: .html_url, status: .status}'")]
    [InlineData("git -C space-invaders-game status --short && git -C space-invaders-game branch --show-current")]
    [InlineData("gh auth status || gh api repos/jay23606/space-invaders-game/pages --jq '{url: .html_url, status: .status}'")]
    [InlineData("Start-Sleep -Seconds 10 && gh api repos/jay23606/space-invaders-game/pages --jq \"{url: .html_url, status: .status}\"")]
    [InlineData("Start-Sleep -Seconds 10; gh api repos/jay23606/space-invaders-game/pages --jq \"{url: .html_url, status: .status, source: .source}\"")]
    public void Allows_narrow_read_only_git_and_github_checks(string command)
    {
        Assert.True(AutoSafeCommandClassifier.IsSafe(command));
    }

    [Theory]
    [InlineData("git -C space-invaders-game add game.js")]
    [InlineData("git -C space-invaders-game commit -m 'update'")]
    [InlineData("git push origin main")]
    [InlineData("gh api repos/jay23606/space-invaders-game/pages -X POST")]
    [InlineData("gh api repos/jay23606/space-invaders-game/pages --jq '.token'")]
    [InlineData("git -C ../other-repo status --short")]
    [InlineData("gh auth status; Remove-Item important.txt")]
    [InlineData("gh auth status; Invoke-Expression 'bad'")]
    [InlineData("gh auth status && git add game.js")]
    [InlineData("gh auth status || Remove-Item important.txt")]
    [InlineData("gh auth status &&&& gh auth status")]
    public void Requires_approval_for_writes_unknown_selectors_and_shell_syntax(string command)
    {
        Assert.False(AutoSafeCommandClassifier.IsSafe(command));
    }

    [Theory]
    [InlineData("git status --short", "git -c core.fsmonitor= status --short")]
    [InlineData("git.exe -C sample branch --show-current", "git.exe -c core.fsmonitor= -C sample branch --show-current")]
    [InlineData("gh auth status && git -C sample status --short", "gh auth status && git -c core.fsmonitor= -C sample status --short")]
    [InlineData("gh auth status; gh api repos/example/project/pages", "gh auth status; gh api repos/example/project/pages")]
    [InlineData("gh auth status && git add file.txt", "gh auth status && git add file.txt")]
    public void Adds_an_empty_fsmonitor_override_only_to_recognized_git_inspections(string command, string expected)
    {
        Assert.Equal(expected, AutoSafeCommandClassifier.PrepareApprovedExecutionCommand(command));
    }
}

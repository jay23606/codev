using Codev;

namespace Codev.Tests;

public sealed class GitDiffHunkSelectorTests
{
    private const string Diff =
        "STAGED CHANGES\n" +
        "diff --git a/file.txt b/file.txt\nindex 1111111..2222222 100644\n--- a/file.txt\n+++ b/file.txt\n" +
        "@@ -1,1 +1,1 @@\n-old staged\n+new staged\n" +
        "@@ -20,1 +20,1 @@\n-old second\n+new second\n" +
        "UNSTAGED CHANGES\n" +
        "diff --git a/file.txt b/file.txt\nindex 2222222..3333333 100644\n--- a/file.txt\n+++ b/file.txt\n" +
        "@@ -1,1 +1,1 @@\n-new staged\n+new working tree\n";

    [Fact]
    public void Selects_only_the_hunk_containing_the_text_selection()
    {
        var selectedText = "+new second";
        var start = Diff.IndexOf(selectedText, StringComparison.Ordinal);

        Assert.True(GitDiffHunkSelector.TrySelect(Diff, start, selectedText.Length, out var selection));
        Assert.NotNull(selection);
        Assert.Equal(GitDiffHunkSection.Staged, selection.Section);
        Assert.Contains("-old second", selection.Patch, StringComparison.Ordinal);
        Assert.DoesNotContain("-old staged", selection.Patch, StringComparison.Ordinal);
        Assert.StartsWith("diff --git a/file.txt b/file.txt", selection.Patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Selects_unstaged_hunks_separately_from_staged_hunks()
    {
        var selectedText = "+new working tree";
        var start = Diff.IndexOf(selectedText, StringComparison.Ordinal);

        Assert.True(GitDiffHunkSelector.TrySelect(Diff, start, selectedText.Length, out var selection));
        Assert.NotNull(selection);
        Assert.Equal(GitDiffHunkSection.Unstaged, selection.Section);
        Assert.Contains("-new staged", selection.Patch, StringComparison.Ordinal);
        Assert.DoesNotContain("-old second", selection.Patch, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("diff --git a/file.txt", 10)]
    [InlineData("-old staged", 80)]
    public void Rejects_selections_that_cross_hunk_boundaries_or_start_in_diff_headers(string selectedText, int length)
    {
        var start = Diff.IndexOf(selectedText, StringComparison.Ordinal);

        Assert.False(GitDiffHunkSelector.TrySelect(Diff, start, length, out _));
    }

    [Fact]
    public void Rejects_untracked_file_hunks()
    {
        const string untracked = "UNTRACKED FILE\ndiff --git a/new.txt b/new.txt\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,1 @@\n+new\n";
        var start = untracked.IndexOf("+new", StringComparison.Ordinal);

        Assert.False(GitDiffHunkSelector.TrySelect(untracked, start, 4, out _));
    }
}

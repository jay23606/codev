namespace Codev.Tests;

public sealed class UnifiedDiffTests
{
    [Fact]
    public void Reports_changed_lines_and_keeps_shared_context()
    {
        var diff = UnifiedDiff.Compare("first\nold\nlast\n", "first\nnew\nlast\n");

        Assert.Collection(diff,
            line => Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Context, "first"), line),
            line => Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Removed, "old"), line),
            line => Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Added, "new"), line),
            line => Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Context, "last"), line));
    }

    [Fact]
    public void Handles_new_and_empty_files_and_emits_unified_ranges()
    {
        var diff = UnifiedDiff.Format("src/new.cs", "", "class New { }\n");

        Assert.Contains("--- a/src/new.cs", diff);
        Assert.Contains("+++ b/src/new.cs", diff);
        Assert.Contains("@@ -0,0 +1,1 @@", diff);
        Assert.Contains("+class New { }", diff);
    }

    [Fact]
    public void Bounded_fallback_preserves_matching_prefix_and_suffix()
    {
        var diff = UnifiedDiff.Compare("a\nb\nc", "a\nB\nc", maxLcsCells: 1);

        Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Context, "a"), diff[0]);
        Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Removed, "b"), diff[1]);
        Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Added, "B"), diff[2]);
        Assert.Equal(new UnifiedDiffLine(UnifiedDiffLineKind.Context, "c"), diff[3]);
    }

    [Fact]
    public void Identical_text_has_no_additions_or_deletions()
    {
        var diff = UnifiedDiff.Compare("same\ntext", "same\ntext");

        Assert.All(diff, line => Assert.Equal(UnifiedDiffLineKind.Context, line.Kind));
        Assert.Contains("No differences.", UnifiedDiff.Format("same.txt", "same\ntext", "same\ntext"));
    }
}

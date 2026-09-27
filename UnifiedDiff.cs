using System.Text;

namespace Codev;

public enum UnifiedDiffLineKind { Context, Added, Removed }

public sealed record UnifiedDiffLine(UnifiedDiffLineKind Kind, string Text);

/// <summary>Creates a bounded, whole-file unified diff suitable for review before applying local edits.</summary>
public static class UnifiedDiff
{
    private const long MaxLcsCells = 4_000_000;

    public static IReadOnlyList<UnifiedDiffLine> Compare(string before, string after, long maxLcsCells = MaxLcsCells)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               oldLines[oldLines.Length - suffix - 1] == newLines[newLines.Length - suffix - 1]) suffix++;

        var result = new List<UnifiedDiffLine>(oldLines.Length + newLines.Length);
        for (var i = 0; i < prefix; i++) result.Add(new(UnifiedDiffLineKind.Context, oldLines[i]));

        var oldCount = oldLines.Length - prefix - suffix;
        var newCount = newLines.Length - prefix - suffix;
        if ((long)(oldCount + 1) * (newCount + 1) <= maxLcsCells)
        {
            var lcs = new int[oldCount + 1, newCount + 1];
            for (var i = oldCount - 1; i >= 0; i--)
                for (var j = newCount - 1; j >= 0; j--)
                    lcs[i, j] = oldLines[prefix + i] == newLines[prefix + j]
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            var oldIndex = 0;
            var newIndex = 0;
            while (oldIndex < oldCount || newIndex < newCount)
            {
                if (oldIndex < oldCount && newIndex < newCount && oldLines[prefix + oldIndex] == newLines[prefix + newIndex])
                {
                    result.Add(new(UnifiedDiffLineKind.Context, oldLines[prefix + oldIndex]));
                    oldIndex++;
                    newIndex++;
                }
                else if (newIndex < newCount && (oldIndex == oldCount || lcs[oldIndex, newIndex + 1] > lcs[oldIndex + 1, newIndex]))
                {
                    result.Add(new(UnifiedDiffLineKind.Added, newLines[prefix + newIndex++]));
                }
                else
                {
                    result.Add(new(UnifiedDiffLineKind.Removed, oldLines[prefix + oldIndex++]));
                }
            }
        }
        else
        {
            for (var i = 0; i < oldCount; i++) result.Add(new(UnifiedDiffLineKind.Removed, oldLines[prefix + i]));
            for (var i = 0; i < newCount; i++) result.Add(new(UnifiedDiffLineKind.Added, newLines[prefix + i]));
        }

        for (var i = suffix; i > 0; i--) result.Add(new(UnifiedDiffLineKind.Context, oldLines[oldLines.Length - i]));
        return result;
    }

    public static string Format(string relativePath, string before, string after)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        var diff = Compare(before, after);
        var output = new StringBuilder();
        output.Append("--- a/").AppendLine(relativePath.Replace('\\', '/'));
        output.Append("+++ b/").AppendLine(relativePath.Replace('\\', '/'));
        if (diff.All(line => line.Kind == UnifiedDiffLineKind.Context))
        {
            output.AppendLine("No differences.");
            return output.ToString();
        }

        var oldStart = oldLines.Length == 0 ? 0 : 1;
        var newStart = newLines.Length == 0 ? 0 : 1;
        output.Append("@@ -").Append(oldStart).Append(',').Append(oldLines.Length)
            .Append(" +").Append(newStart).Append(',').Append(newLines.Length).AppendLine(" @@");
        foreach (var line in diff)
        {
            output.Append(line.Kind switch
            {
                UnifiedDiffLineKind.Added => '+',
                UnifiedDiffLineKind.Removed => '-',
                _ => ' '
            }).AppendLine(line.Text);
        }
        return output.ToString();
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return text.EndsWith('\n') || text.EndsWith('\r') ? lines[..^1] : lines;
    }
}

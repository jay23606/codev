using System.Text.RegularExpressions;

namespace Codev;

/// <summary>Applies strict unified-diff hunks without fuzzy matching or modifying files.</summary>
public static partial class UnifiedDiffApplier
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?: .*)?$")]
    private static partial Regex HunkHeaderRegex();

    public static string Apply(string original, string patch)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (string.IsNullOrWhiteSpace(patch)) throw new InvalidOperationException("Patch is empty.");

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hasFinalNewline = original.EndsWith('\n');
        var source = SplitLines(original);
        var patchLines = SplitLines(patch);
        var output = new List<string>(source.Count);
        var sourceIndex = 0;
        var patchIndex = 0;
        var hunkCount = 0;

        while (patchIndex < patchLines.Count)
        {
            var header = patchLines[patchIndex++];
            if (header.StartsWith("--- ", StringComparison.Ordinal) || header.StartsWith("+++ ", StringComparison.Ordinal) ||
                header.StartsWith("diff ", StringComparison.Ordinal) || header.StartsWith("index ", StringComparison.Ordinal))
                throw new InvalidOperationException("Pass only unified-diff hunk headers and lines; file headers are not accepted.");
            var match = HunkHeaderRegex().Match(header);
            if (!match.Success) throw new InvalidOperationException("Patch contains an invalid unified-diff hunk header.");

            var oldStart = ParseCount(match.Groups[1].Value);
            var oldCount = match.Groups[2].Success ? ParseCount(match.Groups[2].Value) : 1;
            var newCount = match.Groups[4].Success ? ParseCount(match.Groups[4].Value) : 1;
            var targetIndex = oldCount == 0 ? oldStart : oldStart - 1;
            if (targetIndex < sourceIndex || targetIndex > source.Count)
                throw new InvalidOperationException("Patch hunk positions do not match the current file.");
            while (sourceIndex < targetIndex) output.Add(source[sourceIndex++]);

            var consumed = 0;
            var produced = 0;
            while (patchIndex < patchLines.Count && !patchLines[patchIndex].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var line = patchLines[patchIndex++];
                if (line == "\\ No newline at end of file") continue;
                if (line.Length == 0) throw new InvalidOperationException("Patch contains a line without a unified-diff prefix.");
                var text = line[1..];
                switch (line[0])
                {
                    case ' ':
                        MatchSource(text);
                        output.Add(text);
                        consumed++;
                        produced++;
                        break;
                    case '-':
                        MatchSource(text);
                        consumed++;
                        break;
                    case '+':
                        output.Add(text);
                        produced++;
                        break;
                    default:
                        throw new InvalidOperationException("Patch contains a line without a unified-diff prefix.");
                }
            }
            if (consumed != oldCount || produced != newCount)
                throw new InvalidOperationException("Patch hunk line counts do not match its header.");
            hunkCount++;

            void MatchSource(string expected)
            {
                if (sourceIndex >= source.Count || !string.Equals(source[sourceIndex], expected, StringComparison.Ordinal))
                    throw new InvalidOperationException("Patch context does not match the current file; no changes were applied.");
                sourceIndex++;
            }
        }

        if (hunkCount == 0) throw new InvalidOperationException("Patch contains no hunks.");
        output.AddRange(source.Skip(sourceIndex));
        return string.Join(newline, output) + (hasFinalNewline && output.Count > 0 ? newline : "");
    }

    private static int ParseCount(string value) => int.TryParse(value, out var count) && count >= 0
        ? count : throw new InvalidOperationException("Patch contains an invalid line number or count.");

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}

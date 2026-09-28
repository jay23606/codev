namespace Codev;

public static class GitDiffPromptBuilder
{
    public static string AppendSelection(string existingPrompt, string relativePath, string selectedDiff)
    {
        if (string.IsNullOrWhiteSpace(selectedDiff)) throw new ArgumentException("Select some diff lines first.", nameof(selectedDiff));
        var path = string.IsNullOrWhiteSpace(relativePath) ? "selected file" : relativePath.Trim().Replace("`", "\\`", StringComparison.Ordinal);
        var excerpt = selectedDiff.Trim();
        var fenceLength = Math.Max(3, LongestBacktickRun(excerpt) + 1);
        var fence = new string('`', fenceLength);
        var addition = $"Explain this selected Git diff for `{path}` and call out any behavior changes or risks.\n\n{fence}diff\n{excerpt}\n{fence}";
        return string.IsNullOrWhiteSpace(existingPrompt) ? addition : existingPrompt.TrimEnd() + "\n\n" + addition;
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in text)
        {
            if (character == '`') { current++; longest = Math.Max(longest, current); }
            else current = 0;
        }
        return longest;
    }
}

namespace Codev;

public static class GitDiffPromptBuilder
{
    public const int MaxComments = 20;
    public const int MaxDiffLength = 12_000;
    public const int MaxCommentLength = 2_000;

    public static string AppendComments(string existingPrompt, IReadOnlyList<GitDiffComment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        if (comments.Count == 0) return existingPrompt.Trim();
        if (comments.Count > MaxComments) throw new ArgumentException($"At most {MaxComments} diff comments can be sent at once.", nameof(comments));
        var sections = comments.Select((comment, index) =>
        {
            if (string.IsNullOrWhiteSpace(comment.RelativePath) || string.IsNullOrWhiteSpace(comment.SelectedDiff) || string.IsNullOrWhiteSpace(comment.Comment))
                throw new ArgumentException("Each diff comment needs a file, selected diff, and comment.", nameof(comments));
            if (comment.SelectedDiff.Length > MaxDiffLength || comment.Comment.Length > MaxCommentLength)
                throw new ArgumentException("A diff comment is longer than the supported limit.", nameof(comments));
            var excerpt = comment.SelectedDiff.Trim();
            var fence = new string('`', Math.Max(3, LongestBacktickRun(excerpt) + 1));
            var path = comment.RelativePath.Trim().Replace("`", "\\`", StringComparison.Ordinal);
            return $"### Diff comment {index + 1} · `{path}`\n{fence}diff\n{excerpt}\n{fence}\nComment: {comment.Comment.Trim()}";
        });
        var addition = "Please address these comments on the selected Git diff lines:\n\n" + string.Join("\n\n", sections);
        return string.IsNullOrWhiteSpace(existingPrompt) ? addition : existingPrompt.TrimEnd() + "\n\n" + addition;
    }

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

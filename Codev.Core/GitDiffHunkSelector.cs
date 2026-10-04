namespace Codev;

public enum GitDiffHunkSection
{
    Staged,
    Unstaged
}

public enum GitDiffHunkAction
{
    Stage,
    Unstage,
    Revert
}

public sealed record GitDiffHunkSelection(GitDiffHunkSection Section, string Patch, int Start, int Length);

/// <summary>Finds exactly one selected hunk in the single-file diff shown by the Git review pane.</summary>
public static class GitDiffHunkSelector
{
    public const int MaxPatchCharacters = 1_000_000;
    private const string StagedMarker = "STAGED CHANGES";
    private const string UnstagedMarker = "UNSTAGED CHANGES";
    private const string UntrackedMarker = "UNTRACKED FILE";

    public static bool TrySelect(string? fileDiff, int selectionStart, int selectionLength, out GitDiffHunkSelection? selection)
    {
        selection = null;
        if (string.IsNullOrEmpty(fileDiff) || fileDiff.Length > MaxPatchCharacters || selectionStart < 0 ||
            selectionLength <= 0 || selectionStart >= fileDiff.Length || selectionStart + (long)selectionLength > fileDiff.Length)
            return false;

        var markers = FindSectionMarkers(fileDiff);
        var sectionIndex = markers.FindLastIndex(marker => marker.Start <= selectionStart);
        if (sectionIndex < 0 || markers[sectionIndex].Name == UntrackedMarker) return false;
        var section = markers[sectionIndex];
        var sectionEnd = sectionIndex + 1 < markers.Count ? markers[sectionIndex + 1].Start : fileDiff.Length;
        var hunkStarts = FindHunkStarts(fileDiff, section.ContentStart, sectionEnd);
        var hunkIndex = hunkStarts.FindLastIndex(start => start <= selectionStart);
        if (hunkIndex < 0) return false;
        var hunkStart = hunkStarts[hunkIndex];
        var hunkEnd = hunkIndex + 1 < hunkStarts.Count ? hunkStarts[hunkIndex + 1] : sectionEnd;
        var selectionEnd = selectionStart + selectionLength;
        if (selectionStart < hunkStart || selectionEnd > hunkEnd) return false;

        var patch = fileDiff[section.ContentStart..hunkStarts[0]] + fileDiff[hunkStart..hunkEnd];
        if (!patch.TrimStart('\r', '\n').StartsWith("diff --git ", StringComparison.Ordinal) || patch.Length > MaxPatchCharacters)
            return false;

        selection = new GitDiffHunkSelection(
            section.Name == StagedMarker ? GitDiffHunkSection.Staged : GitDiffHunkSection.Unstaged,
            patch,
            hunkStart,
            hunkEnd - hunkStart);
        return true;
    }

    private static List<(string Name, int Start, int ContentStart)> FindSectionMarkers(string text)
    {
        var result = new List<(string Name, int Start, int ContentStart)>();
        var position = 0;
        while (position < text.Length)
        {
            var lineEnd = text.IndexOf('\n', position);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text[position..lineEnd].TrimEnd('\r');
            if (line is StagedMarker or UnstagedMarker or UntrackedMarker)
            {
                var contentStart = lineEnd < text.Length ? lineEnd + 1 : lineEnd;
                result.Add((line, position, contentStart));
            }
            position = lineEnd < text.Length ? lineEnd + 1 : text.Length;
        }
        return result;
    }

    private static List<int> FindHunkStarts(string text, int start, int end)
    {
        var result = new List<int>();
        var position = start;
        while (position < end)
        {
            var lineEnd = text.IndexOf('\n', position);
            if (lineEnd < 0 || lineEnd > end) lineEnd = end;
            var line = text[position..lineEnd].TrimEnd('\r');
            if (line.StartsWith("@@ ", StringComparison.Ordinal)) result.Add(position);
            position = lineEnd < end ? lineEnd + 1 : end;
        }
        return result;
    }
}

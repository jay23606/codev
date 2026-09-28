using System.Text;

namespace Codev;

public enum MarkdownTableAlignment { Left, Center, Right }

public sealed record MarkdownTable(IReadOnlyList<string[]> Rows, IReadOnlyList<MarkdownTableAlignment> Alignments);

public static class MarkdownTableParser
{
    public static bool TryParse(IReadOnlyList<string> lines, int headerIndex, out MarkdownTable table, out int lastRowIndex)
    {
        table = new MarkdownTable([], []);
        lastRowIndex = headerIndex;
        if (headerIndex < 0 || headerIndex + 1 >= lines.Count) return false;

        var header = SplitRow(lines[headerIndex]);
        if (header.Count < 2 || !TryAlignments(lines[headerIndex + 1], out var alignments) || alignments.Count != header.Count)
            return false;

        var rows = new List<string[]> { header.ToArray() };
        lastRowIndex = headerIndex + 1;
        while (lastRowIndex + 1 < lines.Count)
        {
            var next = lines[lastRowIndex + 1];
            if (!HasUnescapedPipe(next)) break;
            var cells = SplitRow(next);
            if (cells.Count < 2) break;
            rows.Add(cells.ToArray());
            lastRowIndex++;
        }
        table = new MarkdownTable(rows, alignments);
        return true;
    }

    private static bool TryAlignments(string row, out List<MarkdownTableAlignment> alignments)
    {
        alignments = [];
        var cells = SplitRow(row);
        if (cells.Count < 2) return false;
        foreach (var cell in cells)
        {
            var value = cell.Trim();
            var left = value.StartsWith(':');
            var right = value.EndsWith(':');
            var start = left ? 1 : 0;
            var length = value.Length - start - (right ? 1 : 0);
            if (length < 3 || value.AsSpan(start, length).IndexOfAnyExcept('-') >= 0) return false;
            alignments.Add(left && right ? MarkdownTableAlignment.Center : right ? MarkdownTableAlignment.Right : MarkdownTableAlignment.Left);
        }
        return true;
    }

    private static List<string> SplitRow(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !IsEscaped(text, text.Length - 1)) text = text[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '`' && !IsEscaped(text, index)) inCode = !inCode;
            if (current == '|' && !inCode && !IsEscaped(text, index))
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else if (current == '\\' && index + 1 < text.Length && text[index + 1] == '|')
            {
                cell.Append('|');
                index++;
            }
            else cell.Append(current);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static bool HasUnescapedPipe(string row)
    {
        var inCode = false;
        for (var index = 0; index < row.Length; index++)
        {
            if (row[index] == '`' && !IsEscaped(row, index)) inCode = !inCode;
            if (row[index] == '|' && !inCode && !IsEscaped(row, index)) return true;
        }
        return false;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        for (var previous = index - 1; previous >= 0 && text[previous] == '\\'; previous--) slashes++;
        return slashes % 2 == 1;
    }
}

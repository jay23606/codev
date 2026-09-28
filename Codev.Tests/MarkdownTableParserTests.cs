namespace Codev.Tests;

public sealed class MarkdownTableParserTests
{
    [Fact]
    public void Parses_header_alignment_rows_and_cells_with_escaped_pipes()
    {
        string[] lines =
        [
            "| Name | Notes | Count |",
            "| :--- | :---: | ---: |",
            "| `a|b` | left \\| right | 12 |",
            "After the table"
        ];

        Assert.True(MarkdownTableParser.TryParse(lines, 0, out var table, out var lastRow));

        Assert.Equal(2, lastRow);
        Assert.Equal(["Name", "Notes", "Count"], table.Rows[0]);
        Assert.Equal(["`a|b`", "left | right", "12"], table.Rows[1]);
        Assert.Equal([MarkdownTableAlignment.Left, MarkdownTableAlignment.Center, MarkdownTableAlignment.Right], table.Alignments);
    }

    [Fact]
    public void Does_not_parse_pipe_text_without_a_separator_row()
    {
        string[] lines = ["A | B", "this is ordinary prose"];

        Assert.False(MarkdownTableParser.TryParse(lines, 0, out _, out _));
    }

    [Fact]
    public void Does_not_parse_a_separator_with_the_wrong_column_count()
    {
        string[] lines = ["A | B | C", "--- | ---"];

        Assert.False(MarkdownTableParser.TryParse(lines, 0, out _, out _));
    }
}

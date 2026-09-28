namespace Codev.Tests;

public sealed class CodeSyntaxHighlighterTests
{
    [Fact]
    public void JavaScript_strings_comments_keywords_and_numbers_are_separated()
    {
        var tokens = CodeSyntaxHighlighter.Tokenize("const value = \"https://example.test\"; // note\nreturn 42;", "js");

        Assert.Contains(tokens, token => token.Text == "const" && token.Kind == CodeTokenKind.Keyword);
        Assert.Contains(tokens, token => token.Text == "\"https://example.test\"" && token.Kind == CodeTokenKind.String);
        Assert.Contains(tokens, token => token.Text == "// note" && token.Kind == CodeTokenKind.Comment);
        Assert.Contains(tokens, token => token.Text == "42" && token.Kind == CodeTokenKind.Number);
    }

    [Fact]
    public void Python_triple_quoted_strings_can_contain_comment_markers_and_newlines()
    {
        const string source = "value = \"\"\"line one\n# still a string\nline three\"\"\"\n# actual comment";

        var tokens = CodeSyntaxHighlighter.Tokenize(source, "python");

        Assert.Contains(tokens, token => token.Text.Contains("# still a string", StringComparison.Ordinal) && token.Kind == CodeTokenKind.String);
        Assert.Contains(tokens, token => token.Text == "# actual comment" && token.Kind == CodeTokenKind.Comment);
    }

    [Fact]
    public void Unknown_language_is_preserved_as_plain_text()
    {
        const string source = "const x = 1; // not parsed";

        var token = Assert.Single(CodeSyntaxHighlighter.Tokenize(source, "unknown-language"));

        Assert.Equal(source, token.Text);
        Assert.Equal(CodeTokenKind.Plain, token.Kind);
    }
}

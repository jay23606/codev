namespace Codev.Tests;

public sealed class ProjectFileMentionParserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-file-mention-tests", Guid.NewGuid().ToString("N"));

    public ProjectFileMentionParserTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("@src/", 5, "src/")]
    [InlineData("Review @src/app.cs please", 18, "src/app.cs")]
    [InlineData("look at @", 9, "")]
    public void Finds_file_mention_at_the_caret(string text, int caret, string prefix)
    {
        Assert.True(ProjectFileMentionParser.TryGet(text, caret, out var mention));
        Assert.Equal(prefix, mention.Prefix);
    }

    [Theory]
    [InlineData("user@example.com", 16)]
    [InlineData("@src/app.cs now", 12)]
    [InlineData("plain text", 10)]
    [InlineData("@starts-here", 0)]
    public void Rejects_email_mentions_nonmatching_tokens_and_missing_mentions(string text, int caret)
    {
        Assert.False(ProjectFileMentionParser.TryGet(text, caret, out _));
    }

    [Fact]
    public void Inserts_selected_path_and_preserves_text_after_the_token()
    {
        const string text = "Please inspect @src/a.cs please";
        Assert.True(ProjectFileMentionParser.TryGet(text, 23, out var mention));

        var result = ProjectFileMentionParser.Insert(text, mention, "src/components/a.cs");

        Assert.Equal("Please inspect src/components/a.cs please", result.Text);
        Assert.Equal("Please inspect src/components/a.cs".Length, result.CaretIndex);
    }

    [Fact]
    public void Suggestions_only_include_supported_safe_files_and_respect_context_exclusions()
    {
        Write("src/app.cs");
        Write("src/image.png");
        Write(".env");
        Write("private/notes.cs");
        var service = new WorkspaceFileService(_root, ["private"]);

        var suggestions = ProjectFileMentionSuggestions.Find(service, "src/");

        Assert.Equal([Path.Combine("src", "app.cs")], suggestions);
        Assert.Empty(ProjectFileMentionSuggestions.Find(service, "private/"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private void Write(string relativePath)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "safe test content");
    }
}

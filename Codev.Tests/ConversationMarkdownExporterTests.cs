using System.IO;

namespace Codev.Tests;

public sealed class ConversationMarkdownExporterTests
{
    [Fact]
    public void Exports_conversation_metadata_transcript_and_reviewed_file_summary()
    {
        var conversation = new Conversation
        {
            Title = "Fix startup",
            Model = "devstral-small-2-64k:latest",
            ProjectPath = Path.Combine(Path.GetTempPath(), "sample-project"),
            ContextFiles = [Path.Combine("src", "App.cs")],
            Messages = [new ChatMessage("user", "Find the startup issue."), new ChatMessage("assistant", "I found it in `App.cs`.")],
            FileChanges = [new FileChangeRecord("src/App.cs", "C:\\Users\\someone\\AppData\\Codev\\checkpoints\\private.bak", DateTimeOffset.Parse("2026-09-27T09:30:00-04:00"), "Edit")]
        };

        var markdown = ConversationMarkdownExporter.Export(conversation);

        Assert.Contains("# Fix startup", markdown);
        Assert.Contains("**Model:** devstral-small-2-64k:latest", markdown);
        Assert.Contains("**Project:** sample-project", markdown);
        Assert.Contains("`src\\App.cs`", markdown);
        Assert.Contains("## You", markdown);
        Assert.Contains("Find the startup issue.", markdown);
        Assert.Contains("## Codev", markdown);
        Assert.Contains("## Reviewed file changes", markdown);
        Assert.Contains("`src/App.cs`", markdown);
        Assert.DoesNotContain("private.bak", markdown);
        Assert.DoesNotContain("C:\\Users\\someone", markdown);
    }

    [Fact]
    public void Empty_conversation_exports_a_valid_heading_and_metadata()
    {
        var markdown = ConversationMarkdownExporter.Export(new Conversation());

        Assert.StartsWith("# New conversation", markdown);
        Assert.Contains("**Model:**", markdown);
        Assert.DoesNotContain("## You", markdown);
    }
}

using System.IO;

namespace Codev.Tests;

public sealed class ConversationHtmlExporterTests
{
    [Fact]
    public void Exports_a_self_contained_transcript_with_escaped_content_and_safe_file_summary()
    {
        var conversation = new Conversation
        {
            Title = "<script>alert('x')</script>",
            Model = "coder & helper",
            ProjectPath = Path.Combine(Path.GetTempPath(), "private-project"),
            Messages = [new("user", "<img src=x onerror=alert(1)>\nsecond line"), new("assistant", "Use `safe` & sound.")],
            FileChanges = [new("src/a.cs", "C:\\Users\\private\\checkpoint.bak", DateTimeOffset.UnixEpoch, "Edit")]
        };

        var html = ConversationHtmlExporter.Export(conversation);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("prefers-color-scheme", html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;<br>", html);
        Assert.Contains("coder &amp; helper", html);
        Assert.Contains("private-project", html);
        Assert.Contains("src/a.cs", html);
        Assert.DoesNotContain("onerror=alert(1)>", html);
        Assert.DoesNotContain("checkpoint.bak", html);
        Assert.DoesNotContain("C:\\Users\\private", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Empty_conversation_exports_valid_standalone_html()
    {
        var html = ConversationHtmlExporter.Export(new Conversation());

        Assert.Contains("<h1>New conversation</h1>", html);
        Assert.Contains("<dt>Model</dt>", html);
        Assert.Contains("</html>", html);
        Assert.DoesNotContain("<article", html);
    }
}

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
            Messages =
            [
                new("user", "<img src=x onerror=alert(1)>\nsecond line"),
                new("assistant", "## Safe output\n\nUse **safe** & sound.\n\n```js\nconst answer = 42;\n```\n\n![remote](https://attacker.example/image.png) [bad](javascript:alert(1)) [good](https://example.org)")
            ],
            FileChanges = [new("src/a.cs", "C:\\Users\\private\\checkpoint.bak", DateTimeOffset.UnixEpoch, "Edit")]
        };

        var html = ConversationHtmlExporter.Export(conversation);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("prefers-color-scheme", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("<h2>Safe output</h2>", html);
        Assert.Contains("<strong>safe</strong>", html);
        Assert.Contains("<pre><code class=\"language-js\">", html);
        Assert.Contains("href=\"#\"", html);
        Assert.Contains("href=\"https://example.org\"", html);
        Assert.Contains("src=\"https://attacker.example/image.png\"", html);
        Assert.Contains("coder &amp; helper", html);
        Assert.Contains("private-project", html);
        Assert.Contains("src/a.cs", html);
        Assert.DoesNotContain("onerror=alert(1)>", html);
        Assert.DoesNotContain("checkpoint.bak", html);
        Assert.DoesNotContain("C:\\Users\\private", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("img-src 'none'", html);
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

    [Fact]
    public void Detects_common_secret_patterns_and_redacts_only_selected_values()
    {
        const string token = "sk-abcdefghijklmnopQRSTUV";
        const string assigned = "example-secret-value-123";
        var conversation = new Conversation
        {
            Messages = [new("user", $"API_KEY={assigned}\n{token}\nordinary text")]
        };

        var candidates = ConversationSecretRedactor.FindCandidates(conversation,
            [("+++ b/src/a.cs\n+api_key=" + token, "Reviewed diff · src/a.cs")]);
        Assert.Contains(candidates, candidate => candidate.Value == token && candidate.Location.Contains("You · message 1"));
        Assert.Contains(candidates, candidate => candidate.Value == assigned);
        Assert.All(candidates, candidate => Assert.DoesNotContain(candidate.Value, candidate.Display));

        var diff = "+const apiKey = \"" + token + "\";";
        var html = ConversationHtmlExporter.Export(conversation, [token], new Dictionary<string, string> { ["src/a.cs"] = diff });

        Assert.Contains("[REDACTED]", html);
        Assert.DoesNotContain(token, html);
        Assert.Contains(assigned, html);
        Assert.Contains("ordinary text", html);
        Assert.Contains("Codev-managed file diffs", html);
        Assert.Contains("src/a.cs", html);
    }

    [Fact]
    public void Renders_reviewed_diffs_as_escaped_code()
    {
        var html = ConversationHtmlExporter.Export(new Conversation(), reviewedDiffs:
            new Dictionary<string, string> { ["src/<unsafe>.cs"] = "<script>alert(1)</script>" });

        Assert.Contains("Codev-managed file diffs", html);
        Assert.Contains("src/&lt;unsafe&gt;.cs", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }
}

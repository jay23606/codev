using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Codev;

/// <summary>Exports a conversation as a self-contained, offline-readable HTML transcript.</summary>
public static class ConversationHtmlExporter
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private static readonly Regex HtmlLinkPattern = new("\\bhref=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Export(
        Conversation conversation,
        IEnumerable<string>? redactions = null,
        IReadOnlyDictionary<string, string>? reviewedDiffs = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var values = redactions?.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        string SafeText(string? text) => ConversationSecretRedactor.Redact(text, values);
        var title = string.IsNullOrWhiteSpace(conversation.Title) ? "New conversation" : SafeText(conversation.Title.Trim());
        var output = new StringBuilder();
        output.AppendLine("<!doctype html>")
            .AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">")
            .Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine(" · Codev conversation</title>")
            .AppendLine("<style>")
            .AppendLine(Styles)
            .AppendLine("</style></head><body><main>")
            .Append("<header><h1>").Append(WebUtility.HtmlEncode(title)).AppendLine("</h1><dl>")
            .Append("<dt>Model</dt><dd>").Append(WebUtility.HtmlEncode(SafeText(conversation.Model))).AppendLine("</dd>")
            .Append("<dt>Updated</dt><dd><time datetime=\"").Append(conversation.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
            .Append("\">").Append(WebUtility.HtmlEncode(conversation.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm zzz"))).AppendLine("</time></dd>");
        if (!string.IsNullOrWhiteSpace(conversation.ProjectPath))
            output.Append("<dt>Project</dt><dd>").Append(WebUtility.HtmlEncode(SafeText(Path.GetFileName(conversation.ProjectPath)))).AppendLine("</dd>");
        output.AppendLine("</dl></header><section aria-label=\"Conversation\">");
        foreach (var message in conversation.Messages)
        {
            var isUser = message.IsUser;
            var label = isUser ? "You" : "Codev";
            output.Append("<article class=\"").Append(isUser ? "user" : "assistant").Append("\"><h2>")
                .Append(label).Append("</h2><div class=\"content\">")
                .Append(RenderMarkdown(SafeText(message.Content)))
                .AppendLine("</div></article>");
        }
        output.AppendLine("</section>");
        if (conversation.FileChanges.Count > 0)
        {
            output.AppendLine("<section class=\"changes\"><h2>Reviewed file changes</h2><ul>");
            foreach (var change in conversation.FileChanges.OrderBy(change => change.ChangedAt))
                output.Append("<li><strong>").Append(WebUtility.HtmlEncode(SafeText(change.Kind))).Append("</strong> <code>")
                    .Append(WebUtility.HtmlEncode(SafeText(change.RelativePath.Replace('\\', '/')))).Append("</code> · ")
                    .Append(WebUtility.HtmlEncode(change.ChangedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))).AppendLine("</li>");
            output.AppendLine("</ul></section>");
        }
        if (reviewedDiffs is { Count: > 0 })
        {
            output.AppendLine("<section class=\"changes\"><h2>Codev-managed file diffs</h2>");
            foreach (var (path, diff) in reviewedDiffs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                output.Append("<details><summary><code>").Append(WebUtility.HtmlEncode(SafeText(path.Replace('\\', '/'))))
                    .Append("</code></summary><pre><code>").Append(WebUtility.HtmlEncode(SafeText(diff))).AppendLine("</code></pre></details>");
            output.AppendLine("</section>");
        }
        output.AppendLine("</main></body></html>");
        return output.ToString();
    }

    private static string RenderMarkdown(string markdown)
    {
        var html = Markdown.ToHtml(markdown ?? "", MarkdownPipeline);
        return HtmlLinkPattern.Replace(html, match =>
        {
            var destination = WebUtility.HtmlDecode(match.Groups[1].Value);
            return Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
                   uri.Scheme is "http" or "https" or "mailto"
                ? match.Value
                : "href=\"#\"";
        });
    }

    private const string Styles = """
        :root{color-scheme:light dark;font:16px/1.6 system-ui,-apple-system,"Segoe UI",sans-serif;background:#111;color:#eee}
        *{box-sizing:border-box}body{margin:0;padding:32px 18px;background:#111;color:#eee}
        main{max-width:900px;margin:0 auto}header{border-bottom:1px solid #383838;padding:0 0 20px;margin-bottom:24px}
        h1{font-size:1.7rem;line-height:1.25;margin:0 0 14px}dl{display:grid;grid-template-columns:max-content 1fr;gap:2px 14px;margin:0;color:#aaa;font-size:.88rem}
        dd{margin:0;color:#ddd}article{margin:18px 0 26px;max-width:82%}article.user{margin-left:auto;background:#262626;border-radius:16px;padding:14px 18px}
        article h2{font-size:.83rem;font-weight:600;color:#aaa;margin:0 0 5px}.content{overflow-wrap:anywhere}.content pre{overflow:auto;padding:12px;border-radius:8px;background:#080808}.content code{font-family:Consolas,monospace}.content table{border-collapse:collapse;display:block;overflow:auto}.content th,.content td{border:1px solid #555;padding:5px 8px;text-align:left}
        .changes{border-top:1px solid #383838;margin-top:32px;padding-top:16px}.changes h2{font-size:1rem}.changes ul{padding-left:22px}.changes details{margin:10px 0}.changes pre{overflow:auto;padding:12px;border-radius:8px;background:#080808}
        @media(prefers-color-scheme:light){:root,body{background:#fff;color:#242424}header,.changes{border-color:#ddd}dl{color:#666}dd{color:#333}article.user{background:#f0f0f0}article h2{color:#666}}
        @media print{body{padding:0;background:#fff;color:#000}article.user{background:#f2f2f2}}
        """;
}

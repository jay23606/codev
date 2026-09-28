using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Codev;

public static class MarkdownRenderer
{
    public static StackPanel Render(string markdown, Brush foreground, Brush muted, Brush codeBackground, Brush accent, double baseFontSize = 14,
        Brush? keyword = null, Brush? type = null, Brush? stringLiteral = null, Brush? number = null, Brush? comment = null)
    {
        baseFontSize = Math.Clamp(baseFontSize, 12, 22);
        var panel = new StackPanel();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var code = new StringBuilder();
        string? language = null;
        var inCode = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                if (inCode)
                {
                    panel.Children.Add(RenderCode(code.ToString().TrimEnd('\n'), language, codeBackground, muted, accent, baseFontSize, foreground, keyword, type, stringLiteral, number, comment));
                    code.Clear();
                    language = null;
                    inCode = false;
                }
                else
                {
                    inCode = true;
                    language = line.Trim().TrimStart('`', '~').Trim();
                }
                continue;
            }
            if (inCode)
            {
                code.AppendLine(raw);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                if (panel.Children.Count > 0) panel.Children.Add(new Border { Height = 5 });
                continue;
            }
            if (line.Trim() is "---" or "***" or "___")
            {
                panel.Children.Add(new Border { Height = 1, Background = muted, Opacity = 0.35, Margin = new Thickness(0, 7, 0, 8) });
                continue;
            }

            var text = line;
            var fontSize = baseFontSize;
            var weight = FontWeights.Normal;
            var italic = false;
            var margin = new Thickness(0, 0, 0, 5);
            if (text.StartsWith("> ", StringComparison.Ordinal))
            {
                text = text[2..]; italic = true; margin = new Thickness(13, 2, 0, 7);
            }
            else if (text.StartsWith("###### ", StringComparison.Ordinal)) { text = text[7..]; fontSize = baseFontSize; weight = FontWeights.SemiBold; margin = new Thickness(0, 7, 0, 5); }
            else if (text.StartsWith("##### ", StringComparison.Ordinal)) { text = text[6..]; fontSize = baseFontSize * 1.07; weight = FontWeights.SemiBold; margin = new Thickness(0, 8, 0, 5); }
            else if (text.StartsWith("#### ", StringComparison.Ordinal)) { text = text[5..]; fontSize = baseFontSize * 1.14; weight = FontWeights.SemiBold; margin = new Thickness(0, 8, 0, 5); }
            else if (text.StartsWith("### ", StringComparison.Ordinal)) { text = text[4..]; fontSize = baseFontSize * 1.21; weight = FontWeights.SemiBold; margin = new Thickness(0, 9, 0, 5); }
            else if (text.StartsWith("## ", StringComparison.Ordinal)) { text = text[3..]; fontSize = baseFontSize * 1.36; weight = FontWeights.SemiBold; margin = new Thickness(0, 11, 0, 6); }
            else if (text.StartsWith("# ", StringComparison.Ordinal)) { text = text[2..]; fontSize = baseFontSize * 1.57; weight = FontWeights.SemiBold; margin = new Thickness(0, 13, 0, 7); }
            else if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal) || text.StartsWith("+ ", StringComparison.Ordinal))
            { text = "•   " + text[2..]; margin = new Thickness(10, 0, 0, 4); }
            else
            {
                var separator = text.IndexOf(". ", StringComparison.Ordinal);
                if (separator is > 0 and < 5 && int.TryParse(text[..separator], out _))
                    margin = new Thickness(10, 0, 0, 4);
            }

            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fontSize, FontWeight = weight, FontStyle = italic ? FontStyles.Italic : FontStyles.Normal, Foreground = foreground, Margin = margin, LineHeight = baseFontSize * 1.6 };
            AddInlineMarkdown(block, text, foreground, accent, codeBackground);
            panel.Children.Add(block);
        }
        if (inCode) panel.Children.Add(RenderCode(code.ToString().TrimEnd('\n'), language, codeBackground, muted, accent, baseFontSize, foreground, keyword, type, stringLiteral, number, comment));
        return panel;
    }

    private static Border RenderCode(string code, string? language, Brush background, Brush muted, Brush accent, double baseFontSize,
        Brush foreground, Brush? keyword, Brush? type, Brush? stringLiteral, Brush? number, Brush? comment)
    {
        var frame = new Border { Background = background, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 5, 0, 10) };
        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7), LastChildFill = true };
        var languageLabel = new TextBlock { Text = string.IsNullOrWhiteSpace(language) ? "CODE" : language.ToUpperInvariant(), FontSize = 9, Foreground = muted, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(languageLabel, Dock.Left); header.Children.Add(languageLabel);
        var copy = new Button { Content = "Copy", Padding = new Thickness(9, 3, 9, 3), FontSize = 10, Foreground = accent, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += (_, _) => { try { Clipboard.SetText(code); copy.Content = "Copied"; } catch { copy.Content = "Copy failed"; } };
        DockPanel.SetDock(copy, Dock.Right); header.Children.Add(copy);
        stack.Children.Add(header);
        var codeText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = Math.Clamp(baseFontSize - 2, 10, 20), Foreground = foreground, TextWrapping = TextWrapping.NoWrap };
        foreach (var token in CodeSyntaxHighlighter.Tokenize(code, language))
        {
            var color = token.Kind switch
            {
                CodeTokenKind.Keyword => keyword ?? accent,
                CodeTokenKind.Type => type ?? accent,
                CodeTokenKind.String => stringLiteral ?? accent,
                CodeTokenKind.Number => number ?? accent,
                CodeTokenKind.Comment => comment ?? muted,
                _ => foreground
            };
            codeText.Inlines.Add(new Run(token.Text) { Foreground = color });
        }
        stack.Children.Add(new ScrollViewer { Content = codeText, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        frame.Child = stack;
        return frame;
    }

    private static void AddInlineMarkdown(TextBlock target, string text, Brush foreground, Brush accent, Brush codeBackground)
    {
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '`' && TryReadDelimited(text, i, "`", out var end, out var value))
            {
                target.Inlines.Add(new Run(value) { FontFamily = new FontFamily("Consolas"), Foreground = accent, Background = codeBackground });
                i = end; continue;
            }
            if (text[i] == '[' && TryReadLink(text, i, out end, out var label, out var url))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                {
                    var link = new Hyperlink(new Run(label)) { NavigateUri = uri, Foreground = accent };
                    link.RequestNavigate += (_, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                    };
                    target.Inlines.Add(link);
                }
                else target.Inlines.Add(new Run(label) { Foreground = foreground });
                i = end; continue;
            }
            if (text.AsSpan(i).StartsWith("**", StringComparison.Ordinal) && TryReadDelimited(text, i, "**", out end, out value))
            {
                target.Inlines.Add(new Run(value) { FontWeight = FontWeights.SemiBold });
                i = end; continue;
            }
            if (text[i] == '*' && TryReadDelimited(text, i, "*", out end, out value))
            {
                target.Inlines.Add(new Run(value) { FontStyle = FontStyles.Italic });
                i = end; continue;
            }
            var next = i + 1;
            while (next < text.Length && text[next] is not '`' and not '*' and not '[') next++;
            target.Inlines.Add(new Run(text[i..next]) { Foreground = foreground });
            i = next;
        }
    }

    private static bool TryReadDelimited(string text, int start, string marker, out int end, out string content)
    {
        end = start; content = "";
        var close = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        if (close < 0) return false;
        content = text[(start + marker.Length)..close];
        end = close + marker.Length;
        return content.Length > 0;
    }

    private static bool TryReadLink(string text, int start, out int end, out string label, out string url)
    {
        end = start; label = ""; url = "";
        var labelEnd = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (labelEnd < 0) return false;
        var urlEnd = text.IndexOf(')', labelEnd + 2);
        if (urlEnd < 0) return false;
        label = text[(start + 1)..labelEnd]; url = text[(labelEnd + 2)..urlEnd]; end = urlEnd + 1;
        return label.Length > 0;
    }
}

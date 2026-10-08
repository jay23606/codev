using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;

namespace Codev.Avalonia.Tests;

public sealed class MarkdownRenderingTests
{
    [AvaloniaFact]
    public void Stable_markdown_control_renders_common_chat_formatting()
    {
        var viewer = new MarkdownScrollViewer
        {
            Markdown = "# Markdown rendering fixture\n\nA paragraph with **bold text**, `inline code`, and [a link](https://example.com).",
            SelectionEnabled = true
        };
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = viewer
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var renderedText = string.Join(" ", window.GetVisualDescendants()
                .OfType<CTextBlock>()
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

            Assert.Contains("Markdown rendering fixture", renderedText, StringComparison.Ordinal);
            Assert.Contains("bold text", renderedText, StringComparison.Ordinal);
            Assert.Contains("inline code", renderedText, StringComparison.Ordinal);
            Assert.Contains("a link", renderedText, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }
}

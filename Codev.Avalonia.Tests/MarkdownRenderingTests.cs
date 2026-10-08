using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
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

            var capturePath = Environment.GetEnvironmentVariable("CODEV_MARKDOWN_RENDER_CAPTURE");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                using var renderedFrame = new RenderTargetBitmap(new PixelSize(900, 600), new Vector(96, 96));
                renderedFrame.Render(window);
                var fullCapturePath = Path.GetFullPath(capturePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullCapturePath)!);
                using var stream = File.Create(fullCapturePath);
                renderedFrame.Save(stream, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }
}

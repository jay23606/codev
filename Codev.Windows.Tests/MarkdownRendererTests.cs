using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Codev.Windows.Tests;

public sealed class MarkdownRendererTests
{
    [Fact]
    public void Renders_markdown_fences_and_copy_control_as_separate_blocks()
    {
        var hasHeading = false;
        var hasCopy = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rendered = MarkdownRenderer.Render("# Heading\n\n`inline`\n\n```js\nconst x = 1;\n```", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral);
                hasHeading = rendered.Children.OfType<TextBlock>().Any(block => block.Inlines.OfType<Run>().Any(run => run.Text.Contains("Heading", StringComparison.Ordinal)));
                hasCopy = rendered.Children.OfType<Border>().Any(border => FindChild<Button>(border)?.Content?.ToString() == "Copy");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        Assert.True(hasHeading);
        Assert.True(hasCopy);
    }

    [Fact]
    public void Fenced_javascript_runs_receive_syntax_colors()
    {
        var keywordBrush = Brushes.MediumPurple;
        var stringBrush = Brushes.LightGreen;
        var hasColoredKeyword = false;
        var hasColoredString = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rendered = MarkdownRenderer.Render("```javascript\nconst message = \"hello\";\n```", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral,
                    keyword: keywordBrush, stringLiteral: stringBrush);
                var frame = rendered.Children.OfType<Border>().Single();
                var scroll = ((StackPanel)frame.Child!).Children.OfType<ScrollViewer>().Single();
                var runs = ((TextBlock)scroll.Content).Inlines.OfType<Run>().ToArray();
                hasColoredKeyword = runs.Any(run => run.Text == "const" && ReferenceEquals(run.Foreground, keywordBrush));
                hasColoredString = runs.Any(run => run.Text == "\"hello\"" && ReferenceEquals(run.Foreground, stringBrush));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;

        Assert.True(hasColoredKeyword);
        Assert.True(hasColoredString);
    }

    [Fact]
    public void Markdown_tables_render_as_scrollable_grids()
    {
        var columnCount = 0;
        var rowCount = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rendered = MarkdownRenderer.Render("| Model | Context |\n| :--- | ---: |\n| Qwen3-Coder | 64K |", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral);
                var frame = rendered.Children.OfType<Border>().Single();
                var scroll = (ScrollViewer)frame.Child!;
                var grid = (Grid)scroll.Content;
                columnCount = grid.ColumnDefinitions.Count;
                rowCount = grid.RowDefinitions.Count;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;

        Assert.Equal(2, columnCount);
        Assert.Equal(2, rowCount);
    }

    [Fact]
    public void Markdown_text_size_scales_body_and_headings_and_is_bounded()
    {
        double headingSize = 0;
        double bodySize = 0;
        double clampedSize = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rendered = MarkdownRenderer.Render("# Heading\n\nBody", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral, baseFontSize: 18);
                headingSize = rendered.Children.OfType<TextBlock>().First(block => block.Inlines.OfType<Run>().Any(run => run.Text.Contains("Heading", StringComparison.Ordinal))).FontSize;
                bodySize = rendered.Children.OfType<TextBlock>().First(block => block.Inlines.OfType<Run>().Any(run => run.Text.Contains("Body", StringComparison.Ordinal))).FontSize;
                var clamped = MarkdownRenderer.Render("Body", Brushes.White, Brushes.Gray, Brushes.Black, Brushes.Coral, baseFontSize: 100);
                clampedSize = clamped.Children.OfType<TextBlock>().Single().FontSize;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;

        Assert.Equal(18, bodySize);
        Assert.Equal(18 * 1.57, headingSize, 4);
        Assert.Equal(22, clampedSize);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}

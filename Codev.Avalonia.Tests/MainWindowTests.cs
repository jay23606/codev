using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codev;
using Codev.Avalonia.Views;

namespace Codev.Avalonia.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Main_window_loads_and_composer_accepts_keyboard_input()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var composer = Assert.IsType<TextBox>(window.FindControl<TextBox>("ComposerTextBox"));
            composer.Focus();

            window.KeyTextInput("Headless UI smoke");

            Assert.Equal("Headless UI smoke", composer.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tool_output_details_start_collapsed()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var message = new ChatMessage("assistant", "**run command**\n" + UntrustedToolOutput.Format("approved command output", "Exit code: 0", command: "dotnet test"));
            var messages = Assert.IsType<ItemsControl>(window.FindControl<ItemsControl>("MessageList"));
            messages.ItemsSource = new[] { message };
            window.UpdateLayout();
            var details = Assert.Single(window.GetVisualDescendants().OfType<Expander>(),
                expander => expander.Header?.ToString() == "Ran commands");

            Assert.False(details.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }
}

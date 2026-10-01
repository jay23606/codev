using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codev;
using Codev.Avalonia.Views;
using System.Reflection;

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

    [AvaloniaFact]
    public async Task Verification_approval_is_rendered_inline_and_run_once_resolves_the_request()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var proposal = new CodeTaskCommandProposal("node --check game.js", Path.GetTempPath(), "PowerShell", IsVerification: true);
            var request = typeof(MainWindow).GetMethod("ApproveAgentCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = Assert.IsAssignableFrom<Task<ProjectCommandApprovalChoice>>(request.Invoke(window, [proposal]));

            await Dispatcher.UIThread.InvokeAsync(() => { });

            var panel = Assert.IsType<Border>(window.FindControl<Border>("InlineApprovalPanel"));
            Assert.True(panel.IsVisible);
            var content = Assert.IsType<ContentControl>(window.FindControl<ContentControl>("InlineApprovalContent"));
            var command = Assert.Single(content.GetVisualDescendants().OfType<TextBox>(), box => box.Text == proposal.Command);
            Assert.True(command.IsReadOnly);
            var runOnce = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Run once & verify");

            await Dispatcher.UIThread.InvokeAsync(() => runOnce.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Equal(ProjectCommandApprovalChoice.RunOnce, await pending);
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}

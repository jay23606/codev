using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Codev.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += async (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel viewModel) await viewModel.SavePendingDraftAsync();
        };
    }

    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Codev.ChatMessage message } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(message.Content);
    }

    private void Composer_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ViewModels.MainViewModel viewModel && !viewModel.IsGenerating && viewModel.SendCommand.CanExecute(null))
            viewModel.SendCommand.Execute(null);
    }

    private async void ModelPicker_DropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel) await viewModel.RefreshModelsAsync();
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var primaryModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (primaryModifier && e.Key == Key.N)
            viewModel.NewConversationCommand.Execute(null);
        else if (primaryModifier && e.Key == Key.F)
            SearchTextBox.Focus();
        else if (primaryModifier && e.Key == Key.L)
            ComposerTextBox.Focus();
        else if (e.Key == Key.Escape && viewModel.IsGenerating)
            viewModel.SendCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }
}

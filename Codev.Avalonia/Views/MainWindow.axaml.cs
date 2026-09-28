using Avalonia.Controls;
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
}

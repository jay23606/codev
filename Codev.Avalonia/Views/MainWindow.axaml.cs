using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Codev.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ViewModels.SpikeMessage message } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(message.Content);
    }
}

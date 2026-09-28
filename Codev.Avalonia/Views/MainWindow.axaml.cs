using Avalonia.Controls;

namespace Codev.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as ViewModels.MainViewModel)?.SavePendingDraft();
    }
}

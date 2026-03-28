using Tysl.Inspection.Desktop.App.ViewModels;

namespace Tysl.Inspection.Desktop.App.Views;

public partial class PlayWin : System.Windows.Window
{
    private bool hasLoaded;

    public PlayWin()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (hasLoaded)
        {
            return;
        }

        hasLoaded = true;
        if (DataContext is PlayWinViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

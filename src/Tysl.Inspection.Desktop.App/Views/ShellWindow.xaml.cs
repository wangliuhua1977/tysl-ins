namespace Tysl.Inspection.Desktop.App.Views;

public partial class ShellWindow : System.Windows.Window
{
    public ShellWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.ShellWindowViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }
}

using Tysl.Inspection.Desktop.App.ViewModels;

namespace Tysl.Inspection.Desktop.App.Views.Pages;

public partial class PreviewPageView : System.Windows.Controls.UserControl
{
    private bool hasLoaded;

    public PreviewPageView()
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
        if (DataContext is PreviewPageViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }
}

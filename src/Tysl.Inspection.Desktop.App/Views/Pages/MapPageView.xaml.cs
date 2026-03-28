using System.IO;

namespace Tysl.Inspection.Desktop.App.Views.Pages;

public partial class MapPageView : System.Windows.Controls.UserControl
{
    private bool hasInitialized;

    public MapPageView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (hasInitialized)
        {
            return;
        }

        hasInitialized = true;
        var placeholderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MapHostPlaceholder.html");
        if (File.Exists(placeholderPath))
        {
            MapHost.Source = new Uri(placeholderPath);
            return;
        }

        MapHost.NavigateToString("<html><body><h3>Map host placeholder</h3></body></html>");
    }
}

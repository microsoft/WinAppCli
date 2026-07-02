using System.Windows;
using System.Windows.Media;
using Windows.ApplicationModel;

namespace sparse_app;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// Queries package identity to demonstrate that the sparse identity package is registered.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        try
        {
            // Package.Current throws InvalidOperationException when the process has no
            // package identity (i.e. the sparse identity package isn't registered, or the
            // exe's <msix> fusion-manifest element is missing/mismatched).
            var package = Package.Current;
            StatusTextBlock.Text = "✅ Running with package identity";
            StatusTextBlock.Foreground = Brushes.Green;
            DetailTextBlock.Text =
                $"Family Name: {package.Id.FamilyName}\n" +
                $"Full Name: {package.Id.FullName}\n" +
                $"Publisher: {package.Id.Publisher}";
        }
        catch (InvalidOperationException)
        {
            StatusTextBlock.Text = "⚠ No package identity";
            StatusTextBlock.Foreground = Brushes.OrangeRed;
            DetailTextBlock.Text =
                "This exe is running unpackaged. Register the sparse identity package with:\n" +
                "Add-AppxPackage -Path sparse-app.identity.msix -ExternalLocation <install-dir>\n" +
                "and make sure the exe's manifest was updated with 'winapp embed-identity'.";
        }
    }
}

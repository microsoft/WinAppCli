using Microsoft.UI.Xaml;

namespace winui_unpackaged_app;

public sealed partial class MainWindow : Window
{
    private int _count;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void CounterButton_Click(object sender, RoutedEventArgs e)
    {
        _count++;
        CounterText.Text = $"Count: {_count}";
    }
}

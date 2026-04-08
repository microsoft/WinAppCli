using Microsoft.UI.Xaml;

namespace winui_app;

public sealed partial class MainWindow : Window
{
    private int _count;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
    }

    private void CounterButton_Click(object sender, RoutedEventArgs e)
    {
        _count++;
        CounterText.Text = $"Count: {_count}";
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var inputText = InputTextBox.Text;
        var isEnabled = FeatureCheckBox.IsChecked == true;
        ResultText.Text = $"Submitted: {inputText} (Feature: {(isEnabled ? "On" : "Off")})";
    }
}

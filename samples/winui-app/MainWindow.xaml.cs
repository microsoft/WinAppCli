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

        // Seed the RichEditBox with known text so the e2e "get-value (TextPattern read path)"
        // assertion verifies real content instead of just a zero exit code on an empty control.
        RichInputBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, "Rich text read path OK");
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

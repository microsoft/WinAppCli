using Microsoft.UI.Xaml;
using winui_solution_core;

namespace winui_solution_app;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        CoreGreetingText.Text = GreetingProvider.GetGreeting();
    }
}

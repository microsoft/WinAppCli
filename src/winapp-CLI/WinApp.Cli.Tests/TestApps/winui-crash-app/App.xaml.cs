using Microsoft.UI.Xaml;
using System;

namespace winui_crash_app;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // Auto-crash mode: if environment variable is set, crash after 2 seconds.
        // Usage with winapp: winapp run <dir> --debug-output
        // Then set WINUI_CRASH_AUTO=1 before running, or use --args.
        var autoCrash = Environment.GetEnvironmentVariable("WINUI_CRASH_AUTO");
        if (autoCrash == "1")
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                unsafe { *(int*)0 = 42; }
            };
            timer.Start();
        }
    }
}

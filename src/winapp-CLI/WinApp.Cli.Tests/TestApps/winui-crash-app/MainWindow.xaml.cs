using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace winui_crash_app;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private unsafe void AccessViolationButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Triggering access violation...";
        // Write to null pointer — produces a clean ACCESS_VIOLATION (0xC0000005)
        *(int*)0 = 42;
    }

    private void StackOverflowButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Triggering stack overflow...";
        InfiniteRecursion(0);
    }

    private void InfiniteRecursion(int depth)
    {
        // Prevent tail-call optimization with the side effect
        StatusText.Text = $"Depth: {depth}";
        InfiniteRecursion(depth + 1);
    }

    private void ManagedExceptionButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Triggering NullReferenceException...";
        object? obj = null;
        _ = obj!.ToString();
    }

    private void TimedCrashButton_Click(object sender, RoutedEventArgs e)
    {
        TimedCrashButton.IsEnabled = false;
        StatusText.Text = "Crashing in 3 seconds...";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int countdown = 3;

        timer.Tick += (s, args) =>
        {
            countdown--;
            CountdownText.Text = $"{countdown}...";

            if (countdown <= 0)
            {
                timer.Stop();
                // Trigger access violation on the UI thread after delay
                unsafe { *(int*)0 = 42; }
            }
        };

        timer.Start();
    }
}

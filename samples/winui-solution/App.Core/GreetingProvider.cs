using Microsoft.UI.Xaml;

namespace winui_solution_core;

/// <summary>
/// A trivial type consumed by the App project so the ProjectReference is real (the reference must
/// be used, otherwise the compiler may prune it and the PRI merge would not be exercised).
/// </summary>
public static class GreetingProvider
{
    public static string GetGreeting()
    {
        var resources = new ResourceDictionary
        {
            Source = new System.Uri("ms-appx:///App.Core/Themes/Generic.xaml"),
        };

        return resources.TryGetValue("CoreGreeting", out var value) && value is string s
            ? s
            : "Hello from App.Core";
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class FrameworkHintTests
{
    [TestMethod]
    [DataRow("WinUIDesktopWin32WindowClass")]                       // WinUI 3 desktop top-level
    [DataRow("Windows.UI.Core.CoreWindow")]                         // UWP / XAML CoreWindow
    [DataRow("Microsoft.UI.Content.ContentIsland")]                 // WinUI content island
    [DataRow("Windows.UI.Composition.DesktopWindowContentBridge")] // XAML island bridge
    [DataRow("SomeXamlHostThing")]                                  // contains "Xaml"
    public void IsXamlClassName_XamlWindows_ReturnTrue(string className)
        => Assert.IsTrue(FrameworkHint.IsXamlClassName(className));

    [TestMethod]
    [DataRow("Chrome_WidgetWin_1")]   // Electron / Chromium
    [DataRow("HwndWrapper[App;;guid]")] // WPF
    [DataRow("Notepad")]              // Win32
    [DataRow("ConsoleWindowClass")]   // Win32 console
    [DataRow("#32770")]               // Win32 dialog
    public void IsXamlClassName_NonXamlWindows_ReturnFalse(string className)
        => Assert.IsFalse(FrameworkHint.IsXamlClassName(className));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void IsXamlClassName_NullOrEmpty_ReturnsFalse(string? className)
        => Assert.IsFalse(FrameworkHint.IsXamlClassName(className));
}

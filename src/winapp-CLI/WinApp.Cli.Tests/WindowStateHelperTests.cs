// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowStateHelperTests
{
    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [TestMethod]
    public void RestoreIfMinimized_NullHandle_ReturnsFalse()
    {
        Assert.IsFalse(WindowStateHelper.RestoreIfMinimized(0),
            "A null (0) window handle has nothing to restore and must be a no-op.");
    }

    [TestMethod]
    public void RestoreIfMinimized_NonMinimizedWindow_ReturnsFalse()
    {
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd == 0)
        {
            Assert.Inconclusive("No console window available — cannot exercise a non-minimized window.");
        }

        // The test host's console window is not minimized, so RestoreIfMinimized must leave it
        // untouched and report that no restore was performed.
        Assert.IsFalse(WindowStateHelper.RestoreIfMinimized(consoleHwnd),
            "A window that isn't minimized must not be restored.");
    }
}

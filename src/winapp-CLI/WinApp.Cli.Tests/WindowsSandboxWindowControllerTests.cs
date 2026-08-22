// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowsSandboxWindowControllerTests
{
    [TestMethod]
    public void SelectNewWindow_IgnoresPreexistingAndHandlelessProcesses()
    {
        var selected = WindowsSandboxWindowController.SelectNewWindow(
            new HashSet<int> { 10, 11 },
            [
                (10, (nint)100),
                (12, nint.Zero),
                (13, (nint)300),
            ]);

        Assert.AreEqual((nint)300, selected);
    }
}

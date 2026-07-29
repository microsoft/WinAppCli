// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
public class MigrateScaffoldCommandTests : MigrateCommandTestBase
{
    [TestMethod]
    public async Task Scaffold_CopiesSourceFilesIntoTarget()
    {
        var source = _tempDirectory.CreateSubdirectory("scaffold-src");
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "MainPage.xaml"),
            "<Page />", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "MainPage.xaml.cs"),
            "using Windows.UI.Xaml;\nnamespace Sample { public sealed partial class MainPage { } }",
            TestContext.CancellationToken);

        var target = _tempDirectory.CreateSubdirectory("scaffold-target");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "==> winapp migrate scaffold");
        StringAssert.Contains(output, "Copied 2 source files");
        StringAssert.Contains(output, "=== SCAFFOLD COMPLETE ===");

        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "MainPage.xaml")),
            "XAML source should be copied into the target scaffold");
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "MainPage.xaml.cs")),
            "code-behind should be copied into the target scaffold");
    }

    [TestMethod]
    public async Task Scaffold_RewritesWindowsUiXamlNamespace()
    {
        var source = _tempDirectory.CreateSubdirectory("scaffold-ns-src");
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "Widget.cs"),
            "using Windows.UI.Xaml.Controls;\nnamespace Sample { public class Widget { } }",
            TestContext.CancellationToken);

        var target = _tempDirectory.CreateSubdirectory("scaffold-ns-target");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, _) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(0, exit);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Widget.cs"), TestContext.CancellationToken);
        StringAssert.Contains(migrated, "Microsoft.UI.Xaml.Controls");
        Assert.IsFalse(migrated.Contains("Windows.UI.Xaml"), "UWP namespace should be rewritten");
    }
}

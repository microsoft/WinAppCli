// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // migrate commands write to the process-wide System.Console.Out
public class MigrateScaffoldCommandTests : MigrateCommandTestBase
{
    private async Task WriteAsync(DirectoryInfo dir, string relative, string content)
    {
        var path = Path.Combine(dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.CancellationToken);
    }

    private async Task<DirectoryInfo> CreateUwpSourceAsync(string name)
    {
        var dir = _tempDirectory.CreateSubdirectory(name);
        await WriteAsync(dir, "App.csproj", CleanCsproj); // makes it look like a UWP project
        return dir;
    }

    private async Task<DirectoryInfo> CreateWinuiTargetAsync(string name)
    {
        var dir = _tempDirectory.CreateSubdirectory(name);
        await WriteAsync(dir, "App.csproj", CleanCsproj);
        await WriteAsync(dir, "MainWindow.xaml", "<Window><Grid Grid.Row=\"1\" /></Window>");
        return dir;
    }

    [TestMethod]
    public async Task Scaffold_CopiesSourceFilesIntoTarget()
    {
        var source = await CreateUwpSourceAsync("scaffold-src");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        await WriteAsync(source, "MainPage.xaml.cs",
            "using Windows.UI.Xaml;\nnamespace Sample { public sealed partial class MainPage { } }");
        var target = await CreateWinuiTargetAsync("scaffold-target");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "==> winapp migrate scaffold");
        StringAssert.Contains(output, "=== SCAFFOLD COMPLETE ===");
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "MainPage.xaml")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "MainPage.xaml.cs")));
    }

    [TestMethod]
    public async Task Scaffold_ExcludesSigningKeysButCopiesContent()
    {
        var source = await CreateUwpSourceAsync("scaffold-pfx-src");
        await WriteAsync(source, "MyApp_TemporaryKey.pfx", "PRIVATE-SIGNING-MATERIAL");
        await WriteAsync(source, "keys\\strong.snk", "SNK");
        await WriteAsync(source, "assets\\data.json", "{ \"ok\": true }");
        var target = await CreateWinuiTargetAsync("scaffold-pfx-target");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "MyApp_TemporaryKey.pfx")),
            "private .pfx signing material must not be copied into the migrated project");
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "keys", "strong.snk")),
            ".snk signing keys must not be copied");
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "assets", "data.json")),
            "ordinary content files must still be copied");
    }

    [TestMethod]
    public async Task Scaffold_RewritesWindowsUiXamlNamespace()
    {
        var source = await CreateUwpSourceAsync("scaffold-ns-src");
        await WriteAsync(source, "Widget.cs",
            "using Windows.UI.Xaml.Controls;\nnamespace Sample { public class Widget { } }");
        var target = await CreateWinuiTargetAsync("scaffold-ns-target");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, _) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(0, exit);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Widget.cs"), TestContext.CancellationToken);
        StringAssert.Contains(migrated, "Microsoft.UI.Xaml.Controls");
        Assert.IsFalse(migrated.Contains("Windows.UI.Xaml"), "UWP namespace should be rewritten");
    }

    [TestMethod]
    public async Task Scaffold_PreservesWinuiStartupFiles()
    {
        var source = await CreateUwpSourceAsync("scaffold-app-src");
        await WriteAsync(source, "App.xaml", "<Application x:Class=\"Uwp.App\" />");
        await WriteAsync(source, "App.xaml.cs",
            "using Windows.UI.Xaml;\nnamespace Uwp { public sealed partial class App { void OnLaunched() { Window.Current.Activate(); } } }");
        var target = await CreateWinuiTargetAsync("scaffold-app-target");
        await WriteAsync(target, "App.xaml", "<Application x:Class=\"WinUi.App\" />"); // the WinUI scaffold's own

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "Preserved WinUI scaffold startup file");
        var appXaml = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "App.xaml"), TestContext.CancellationToken);
        StringAssert.Contains(appXaml, "WinUi.App"); // scaffold startup NOT overwritten by UWP App.xaml
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "App.xaml.cs")),
            "UWP App.xaml.cs must not be copied over the scaffold startup");
    }

    [TestMethod]
    public async Task Scaffold_OverlappingSourceAndTarget_Fails()
    {
        var dir = await CreateWinuiTargetAsync("scaffold-overlap");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, dir.FullName, "--target", dir.FullName, "--from-uwp");

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR]");
    }

    [TestMethod]
    public async Task Scaffold_SourceWithoutProject_Fails()
    {
        var source = _tempDirectory.CreateSubdirectory("scaffold-noproj-src");
        await WriteAsync(source, "MainPage.xaml.cs", "namespace Sample { class MainPage { } }");
        var target = await CreateWinuiTargetAsync("scaffold-noproj-target");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Source is not a UWP project");
    }

    [TestMethod]
    public async Task Scaffold_TargetWithoutScaffold_Fails()
    {
        var source = await CreateUwpSourceAsync("scaffold-notarget-src");
        var target = _tempDirectory.CreateSubdirectory("scaffold-notarget-target"); // empty

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp");

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Target is not a WinUI 3 scaffold");
    }

    [TestMethod]
    public async Task Scaffold_Quiet_SuppressesProgressOutput()
    {
        var source = await CreateUwpSourceAsync("scaffold-quiet-src");
        await WriteAsync(source, "MainPage.xaml.cs", "namespace S { class MainPage { } }");
        var target = await CreateWinuiTargetAsync("scaffold-quiet-target");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, source.FullName, "--target", target.FullName, "--from-uwp", "--quiet");

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(output.Contains("=== SCAFFOLD COMPLETE ==="), $"progress should be suppressed under --quiet, got: {output}");
        Assert.IsFalse(output.Contains("==> winapp migrate scaffold"), "banner should be suppressed under --quiet");
    }

    [TestMethod]
    public async Task Scaffold_Quiet_StillReportsErrors()
    {
        var dir = await CreateWinuiTargetAsync("scaffold-quiet-overlap");

        var command = GetRequiredService<MigrateScaffoldCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(
            command, dir.FullName, "--target", dir.FullName, "--from-uwp", "--quiet");

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR]");
    }
}

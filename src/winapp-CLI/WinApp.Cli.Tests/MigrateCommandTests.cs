// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class MigrateCommandTests : MigrateCommandTestBase
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
        await WriteAsync(dir, $"{name}.csproj", CleanCsproj);
        await WriteAsync(dir, "Package.appxmanifest", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Capabilities><Capability Name="internetClient" /></Capabilities>
              <Applications><Application><Extensions>
                <uap:Extension Category="windows.appService" />
              </Extensions></Application></Applications>
            </Package>
            """);
        return dir;
    }

    private void ArrangeTemplateCreation(
        DirectoryInfo target,
        string projectName = "MigratedApp",
        Action<DirectoryInfo>? customizeTemplate = null)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            Assert.IsTrue(arguments.Contains("new winui", StringComparison.Ordinal), arguments);
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, $"{projectName}.csproj"), CleanCsproj);
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml"),
                $"<Application x:Class=\"{projectName}.App\"><Application.Resources><ResourceDictionary /></Application.Resources></Application>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml.cs"),
                $"namespace {projectName}; public partial class App {{ }}");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml"),
                $"<Window x:Class=\"{projectName}.MainWindow\"><Grid Grid.Row=\"1\" /></Window>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml.cs"),
                $"namespace {projectName}; public partial class MainWindow {{ public MainWindow() {{ InitializeComponent(); }} }}");
            customizeTemplate?.Invoke(templateOutput);
            return (0, "Template created.", string.Empty);
        };
    }

    private static DirectoryInfo GetTemplateOutput(string arguments)
    {
        var tokens = WindowsCommandLine.SplitArguments(arguments);
        var outputIndex = tokens.ToList().IndexOf("--output");
        Assert.IsTrue(outputIndex >= 0 && outputIndex + 1 < tokens.Count, "Template command must specify --output.");
        return new DirectoryInfo(tokens[outputIndex + 1]);
    }

    private async Task<(int ExitCode, string Output)> InvokeAsync(DirectoryInfo source, DirectoryInfo target, params string[] extra)
    {
        var args = new List<string> { source.FullName, "--output", target.FullName };
        args.AddRange(extra);
        return await InvokeCapturingConsoleAsync(GetRequiredService<MigrateCommand>(), [.. args]);
    }

    [TestMethod]
    public async Task Migrate_CreatesWinuiProjectAndWritesReport()
    {
        var source = await CreateUwpSourceAsync("SensorApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs",
            "using Windows.UI.Xaml.Controls; namespace SDKTemplate; public partial class MainPage { }");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "output"));
        ArrangeTemplateCreation(target, "SensorAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "=== MECHANICAL MIGRATION COMPLETE ===");
        Assert.HasCount(1, FakeDotNet.CommandCalls);
        StringAssert.Contains(FakeDotNet.CommandCalls[0].Arguments, "new winui");
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "SensorAppApp.csproj")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "migration-report.json")));
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "MainPage.xaml.cs"), TestContext.CancellationToken);
        StringAssert.Contains(migrated, "Microsoft.UI.Xaml.Controls");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        Assert.AreEqual("mechanical-migration-complete", report.RootElement.GetProperty("status").GetString());
        Assert.IsTrue(report.RootElement.GetProperty("todos").GetArrayLength() >= 2);
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .All(todo => todo.GetProperty("status").GetString() == "pending"));
    }

    [TestMethod]
    public async Task Migrate_RewritesDispatcherHasThreadAccessWithoutTodo()
    {
        var source = await CreateUwpSourceAsync("DispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage : Page
            {
                void NotifyUser()
                {
                    if (Dispatcher.HasThreadAccess) { }
                }
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "dispatcher-output"));
        ArrangeTemplateCreation(target, "DispatcherApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "MainPage.xaml.cs"), TestContext.CancellationToken);
        StringAssert.Contains(migrated, "DispatcherQueue.HasThreadAccess");
        Assert.IsFalse(migrated.Contains("Dispatcher.HasThreadAccess", StringComparison.Ordinal));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var todos = report.RootElement.GetProperty("todos").EnumerateArray();
        Assert.IsFalse(todos.Any(todo => todo.GetProperty("category").GetString() == "dispatcher"));
    }

    [TestMethod]
    public async Task Migrate_ReportsDispatcherRunAsyncAsKnownResidual()
    {
        var source = await CreateUwpSourceAsync("AsyncDispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                async void Update() =>
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { });
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "async-dispatcher-output"));
        ArrangeTemplateCreation(target, "AsyncDispatcherApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var dispatcherTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "dispatcher");
        Assert.AreEqual("required", dispatcherTodo.GetProperty("priority").GetString());
        Assert.AreEqual(1, dispatcherTodo.GetProperty("locations").GetArrayLength());
        Assert.AreEqual(5, dispatcherTodo.GetProperty("locations")[0].GetProperty("line").GetInt32());
    }

    [TestMethod]
    public async Task Migrate_ReportsWindowCurrentAsKnownResidual()
    {
        var source = await CreateUwpSourceAsync("WindowApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                double Width() => Window.Current.Bounds.Width;
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "window-output"));
        ArrangeTemplateCreation(target, "WindowApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var windowingTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "windowing");
        Assert.AreEqual("required", windowingTodo.GetProperty("priority").GetString());
        Assert.AreEqual(4, windowingTodo.GetProperty("locations")[0].GetProperty("line").GetInt32());
    }

    [TestMethod]
    public async Task Migrate_DoesNotRewriteDispatcherTextOrAmbiguousReceiver()
    {
        var source = await CreateUwpSourceAsync("AmbiguousDispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage : Page
            {
                string Description = "Dispatcher.HasThreadAccess";
                // Dispatcher.HasThreadAccess must not be changed in this comment.
                bool Access() => Dispatcher.HasThreadAccess;
            }
            """);
        await WriteAsync(source, "Worker.cs", """
            public class Worker
            {
                bool Access() => Dispatcher.HasThreadAccess;
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "ambiguous-dispatcher-output"));
        ArrangeTemplateCreation(target, "AmbiguousDispatcherApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var page = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "MainPage.xaml.cs"), TestContext.CancellationToken);
        StringAssert.Contains(page, "\"Dispatcher.HasThreadAccess\"");
        StringAssert.Contains(page, "// Dispatcher.HasThreadAccess must not be changed");
        StringAssert.Contains(page, "DispatcherQueue.HasThreadAccess");
        var worker = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Worker.cs"), TestContext.CancellationToken);
        StringAssert.Contains(worker, "Dispatcher.HasThreadAccess");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var dispatcherTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "dispatcher");
        Assert.IsTrue(dispatcherTodo.GetProperty("locations").EnumerateArray()
            .Any(location => location.GetProperty("path").GetString() == "Worker.cs"));
    }

    [TestMethod]
    public async Task Migrate_PreservesSkippedAppFilesForTodo()
    {
        var source = await CreateUwpSourceAsync("ResourcesApp");
        await WriteAsync(source, "App.xaml", "<Application><Application.Resources><Color x:Key=\"Accent\">Red</Color></Application.Resources></Application>");
        await WriteAsync(source, "App.xaml.cs", "class UwpApp { }");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "resources-output"));
        ArrangeTemplateCreation(target, "ResourcesApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "App.xaml.reference")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "App.xaml.cs.reference")));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "app-resources"));
    }

    [TestMethod]
    public async Task Migrate_TemplateFailureRemovesNewPartialOutput()
    {
        var source = await CreateUwpSourceAsync("FailedTemplateApp");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "failed-output"));
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (1, string.Empty, "Template not installed.");
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Template not installed.");
        Assert.IsFalse(target.Exists);
    }

    [TestMethod]
    public async Task Migrate_MalformedTemplateRestoresExistingEmptyOutput()
    {
        var source = await CreateUwpSourceAsync("MalformedTemplateApp");
        var target = _tempDirectory.CreateSubdirectory("empty-output");
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (0, "Template claimed success.", string.Empty);
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "did not produce the expected project files");
        Assert.IsTrue(target.Exists);
        Assert.IsFalse(target.EnumerateFileSystemInfos().Any());
    }

    [TestMethod]
    public async Task Migrate_QuietRemainsActiveAcrossAsyncTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("QuietApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "quiet-output"));
        FakeDotNet.RunDotnetCommandAsyncHandler = async arguments =>
        {
            await Task.Yield();
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "QuietAppApp.csproj"), CleanCsproj);
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml"),
                "<Application><Application.Resources><ResourceDictionary /></Application.Resources></Application>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml.cs"), "class App { }");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml"), "<Window><Grid Grid.Row=\"1\" /></Window>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml.cs"),
                "class MainWindow { MainWindow() { InitializeComponent(); } }");
            return (0, "Template created.", string.Empty);
        };

        var (exit, output) = await InvokeAsync(source, target, "--quiet");

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(output.Contains("MECHANICAL MIGRATION COMPLETE", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("Copied", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Migrate_NonEmptyOutputFailsBeforeTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("ExistingOutputApp");
        var target = _tempDirectory.CreateSubdirectory("existing-output");
        await WriteAsync(target, "keep.txt", "existing");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Output directory may contain only supported control-plane metadata");
        Assert.IsEmpty(FakeDotNet.CommandCalls);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "keep.txt")));
    }

    [TestMethod]
    public async Task Migrate_MetadataOnlyOutputPreservesMetadataAndMergesScaffold()
    {
        var source = await CreateUwpSourceAsync("MetadataOutputApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = _tempDirectory.CreateSubdirectory("metadata-output");
        await WriteAsync(target, ".git\\HEAD", "ref: refs/heads/main\n");
        await WriteAsync(target, ".github\\instructions\\existing.md", "existing instructions");
        ArrangeTemplateCreation(target, "MetadataOutputApp", template =>
        {
            var instructions = Directory.CreateDirectory(Path.Combine(template.FullName, ".github", "instructions"));
            File.WriteAllText(Path.Combine(instructions.FullName, "template.md"), "template instructions");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.AreEqual("ref: refs/heads/main\n", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".git", "HEAD"), TestContext.CancellationToken));
        Assert.AreEqual("existing instructions", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".github", "instructions", "existing.md"), TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".github", "instructions", "template.md")));
        Assert.IsFalse(Directory.Exists(Path.Combine(target.FullName, ".github", ".github")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "MetadataOutputApp.csproj")));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        Assert.AreEqual(
            target.FullName.Replace('\\', '/'),
            report.RootElement.GetProperty("target").GetProperty("root").GetString());
    }

    [TestMethod]
    public async Task Migrate_MetadataConflictLeavesExistingOutputUnchanged()
    {
        var source = await CreateUwpSourceAsync("MetadataConflictApp");
        var target = _tempDirectory.CreateSubdirectory("metadata-conflict-output");
        await WriteAsync(target, ".git\\HEAD", "original head");
        await WriteAsync(target, ".github\\instructions\\conflict.md", "original instructions");
        ArrangeTemplateCreation(target, "MetadataConflictApp", template =>
        {
            var instructions = Directory.CreateDirectory(Path.Combine(template.FullName, ".github", "instructions"));
            File.WriteAllText(Path.Combine(instructions.FullName, "conflict.md"), "different instructions");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "conflicts with an existing metadata file");
        Assert.AreEqual("original head", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".git", "HEAD"), TestContext.CancellationToken));
        Assert.AreEqual("original instructions", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".github", "instructions", "conflict.md"), TestContext.CancellationToken));
        Assert.AreEqual(2, target.EnumerateFileSystemInfos().Count());
    }

    [TestMethod]
    public async Task Migrate_TemplateFailurePreservesMetadataOnlyOutput()
    {
        var source = await CreateUwpSourceAsync("MetadataFailureApp");
        var target = _tempDirectory.CreateSubdirectory("metadata-failure-output");
        await WriteAsync(target, ".git\\HEAD", "original head");
        await WriteAsync(target, ".github\\instructions\\existing.md", "original instructions");
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (1, string.Empty, "Template failed.");
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Template failed.");
        Assert.AreEqual("original head", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".git", "HEAD"), TestContext.CancellationToken));
        Assert.AreEqual("original instructions", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".github", "instructions", "existing.md"), TestContext.CancellationToken));
        Assert.AreEqual(2, target.EnumerateFileSystemInfos().Count());
    }

    [TestMethod]
    public async Task Migrate_SourceWithoutProjectFails()
    {
        var source = _tempDirectory.CreateSubdirectory("not-uwp");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "invalid-source-output"));

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Source is not a UWP project");
        Assert.IsEmpty(FakeDotNet.CommandCalls);
    }
}

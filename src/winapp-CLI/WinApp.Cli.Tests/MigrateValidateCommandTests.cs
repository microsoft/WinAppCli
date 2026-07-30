// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // migrate commands write to the process-wide System.Console.Out
public class MigrateValidateCommandTests : MigrateCommandTestBase
{
    [TestMethod]
    public async Task Validate_CleanSingleProject_PassesGate()
    {
        var project = await CreateProjectDirAsync("clean");

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "[PASS] Validation gate");
    }

    [TestMethod]
    public async Task Validate_NestedDuplicateProject_FailsGate()
    {
        var project = await CreateProjectDirAsync("nested");
        var appx = project.CreateSubdirectory("AppX");
        await File.WriteAllTextAsync(Path.Combine(appx.FullName, "App.csproj"), CleanCsproj, TestContext.CancellationToken);

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "[FAIL] Nested duplicate project");
        StringAssert.Contains(output, "[FAIL] Validation gate");
    }

    [TestMethod]
    public async Task Validate_EmptyDirectory_FailsGate()
    {
        var project = _tempDirectory.CreateSubdirectory("empty");

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "[FAIL] Project layout");
    }

    [TestMethod]
    public async Task Validate_MissingShell_FailsGate()
    {
        var project = _tempDirectory.CreateSubdirectory("noshell");
        await File.WriteAllTextAsync(Path.Combine(project.FullName, "App.csproj"), CleanCsproj, TestContext.CancellationToken);

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "[FAIL] Shell wiring");
    }

    [TestMethod]
    public async Task Validate_CleanRun_DeletesStaleDiagnostics()
    {
        var project = await CreateProjectDirAsync("stale-diag");
        var diagPath = Path.Combine(project.FullName, ".validator-diagnostics.txt");
        await File.WriteAllTextAsync(diagPath, "stale failures from a previous run", TestContext.CancellationToken);

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(File.Exists(diagPath), "a clean pass must delete the stale diagnostics file");
    }

    [TestMethod]
    public async Task Validate_MarkerInCommentOnly_PassesMarkerGate()
    {
        var project = await CreateProjectDirAsync("marker-comment");
        await File.WriteAllTextAsync(Path.Combine(project.FullName, "Legacy.cs"),
            "// Previously used using Windows.UI.Xaml in the UWP version\nnamespace Sample { public class Legacy { } }",
            TestContext.CancellationToken);

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "[PASS] Residue (markers)");
    }

    [TestMethod]
    public async Task Validate_RealUwpUsing_FailsMarkerGate()
    {
        var project = await CreateProjectDirAsync("marker-real");
        await File.WriteAllTextAsync(Path.Combine(project.FullName, "Bad.cs"),
            "using Windows.UI.Xaml;\nnamespace Sample { public class Bad { } }",
            TestContext.CancellationToken);

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "[FAIL] Residue (markers)");
    }

    [TestMethod]
    public async Task Validate_Quiet_SuppressesPassOutput()
    {
        var project = await CreateProjectDirAsync("validate-quiet");

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName, "--quiet");

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(output.Contains("[PASS]"), $"--quiet should suppress [PASS] chatter, got: {output}");
    }
}

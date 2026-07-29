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
        FakeDriver.DriverFound = true;
        FakeDriver.StdOut = """{"schemaVersion":"1.0","files":[]}""";

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "[PASS] Validation gate");
        StringAssert.Contains(output, "[PASS] Residue (API)");
        Assert.AreEqual(1, FakeDriver.Runs.Count, "validate should invoke the analyzer driver exactly once");
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
    public async Task Validate_MustFixApiResidue_FailsGate()
    {
        var project = await CreateProjectDirAsync("residue");
        FakeDriver.StdOut = """
            {
              "schemaVersion": "1.0",
              "files": [
                {
                  "path": "MainPage.xaml.cs",
                  "findings": [
                    { "id": "WUI0004", "severity": "startup-crash", "detected": "GetForCurrentView",
                      "location": { "file": "MainPage.xaml.cs", "line": 12, "column": 5 } }
                  ]
                }
              ]
            }
            """;

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "[FAIL] Residue (API)");
    }

    [TestMethod]
    public async Task Validate_DriverNotFound_WarnsButPasses()
    {
        var project = await CreateProjectDirAsync("nodriver");
        FakeDriver.DriverFound = false;

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "[WARN] Residue (API)");
        StringAssert.Contains(output, "not found");
        StringAssert.Contains(output, "[PASS] Validation gate");
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
    public async Task Validate_DriverNonzeroExit_FailsGate()
    {
        var project = await CreateProjectDirAsync("nonzero-exit");
        FakeDriver.DriverFound = true;
        FakeDriver.ExitCode = 3;
        FakeDriver.StdOut = """{"schemaVersion":"1.0","files":[]}""";

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "[FAIL] Residue (API)");
        StringAssert.Contains(output, "exited with code 3");
    }

    [TestMethod]
    public async Task Validate_CleanRun_DeletesStaleDiagnostics()
    {
        var project = await CreateProjectDirAsync("stale-diag");
        FakeDriver.DriverFound = true;
        FakeDriver.StdOut = """{"schemaVersion":"1.0","files":[]}""";
        var diagPath = Path.Combine(project.FullName, ".validator-diagnostics.txt");
        await File.WriteAllTextAsync(diagPath, "stale failures from a previous run", TestContext.CancellationToken);

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(File.Exists(diagPath), "a clean pass must delete the stale diagnostics file");
    }

    [TestMethod]
    public async Task Validate_DriverThrows_FailsGate()
    {
        var project = await CreateProjectDirAsync("driver-throws");
        FakeDriver.ThrowOnRun = new InvalidOperationException("driver crashed");

        var command = GetRequiredService<MigrateValidateCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "[FAIL] Residue (API)");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // migrate commands write to the process-wide System.Console.Out
public class MigrateAnalyzeCommandTests : MigrateCommandTestBase
{
    [TestMethod]
    public async Task Analyze_DriverFound_PassesJsonThroughAndReturnsExitCode()
    {
        var project = await CreateProjectDirAsync("analyze-ok");
        FakeDriver.DriverFound = true;
        FakeDriver.ExitCode = 0;
        FakeDriver.StdOut = """{"schemaVersion":"1.0","summary":{"filesAnalyzed":3},"files":[]}""";

        var command = GetRequiredService<MigrateAnalyzeCommand>();
        var (exit, output) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "\"schemaVersion\":\"1.0\"");
        Assert.AreEqual(1, FakeDriver.Runs.Count);
        Assert.IsTrue(FakeDriver.Runs[0].FromUwp, "analyze always runs with fromUwp: true");
    }

    [TestMethod]
    public async Task Analyze_DriverReportsNonZero_PropagatesExitCode()
    {
        var project = await CreateProjectDirAsync("analyze-nonzero");
        FakeDriver.DriverFound = true;
        FakeDriver.ExitCode = 3;
        FakeDriver.StdOut = """{"schemaVersion":"1.0","files":[]}""";
        FakeDriver.StdErr = "driver reported a problem";

        var command = GetRequiredService<MigrateAnalyzeCommand>();
        var (exit, _) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(3, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "driver reported a problem");
    }

    [TestMethod]
    public async Task Analyze_DriverNotFound_ReturnsErrorAndLogs()
    {
        var project = await CreateProjectDirAsync("analyze-nodriver");
        FakeDriver.DriverFound = false;

        var command = GetRequiredService<MigrateAnalyzeCommand>();
        var (exit, _) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "not found");
    }

    [TestMethod]
    public async Task Analyze_DriverThrows_ReturnsErrorAndLogs()
    {
        var project = await CreateProjectDirAsync("analyze-throws");
        FakeDriver.ThrowOnRun = new InvalidOperationException("boom");

        var command = GetRequiredService<MigrateAnalyzeCommand>();
        var (exit, _) = await InvokeCapturingConsoleAsync(command, project.FullName);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "boom");
    }
}

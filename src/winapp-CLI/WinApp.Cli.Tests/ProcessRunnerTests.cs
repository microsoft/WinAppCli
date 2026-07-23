// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Exercises the real <see cref="ProcessRunner"/> against harmless system commands so the
/// security-sensitive process construction, concurrent stream draining, exit-code handling, and
/// cancellation/kill behavior are validated without performing a real Azure login.
/// </summary>
[TestClass]
public class ProcessRunnerTests
{
    private static readonly string CmdExe = Path.Join(Environment.SystemDirectory, "cmd.exe");

    private DirectoryInfo _tempDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Directory.CreateDirectory(
            Path.Join(Path.GetTempPath(), "winapp-processrunner-tests", Guid.NewGuid().ToString("N")));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDirectory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the per-test temp directory; a leftover temp folder is harmless.
        }
    }

    [TestMethod]
    public async Task RunAsync_CapturesStandardOutputAndExitCode()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest(CmdExe, ["/c", "echo", "hello-world"]));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "hello-world");
    }

    [TestMethod]
    public async Task RunAsync_ForwardsEachOutputLineToCallback()
    {
        var runner = new ProcessRunner();
        var lines = new List<string>();

        var result = await runner.RunAsync(
            new ProcessRunRequest(CmdExe, ["/c", "echo", "line-from-callback"]),
            onOutputLine: lines.Add);

        Assert.AreEqual(0, result.ExitCode);
        CollectionAssert.Contains(lines, "line-from-callback");
    }

    [TestMethod]
    public async Task RunAsync_NonZeroExitCode_IsReported()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest(CmdExe, ["/c", "exit", "3"]));

        Assert.AreEqual(3, result.ExitCode);
    }

    [TestMethod]
    public async Task RunAsync_PassesEnvironmentVariablesToChild()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest(
            CmdExe,
            ["/c", "echo", "%WINAPP_TEST_VAR%"],
            Environment: new Dictionary<string, string> { ["WINAPP_TEST_VAR"] = "injected-value" }));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "injected-value");
    }

    [TestMethod]
    public async Task RunAsync_WhenCancelled_KillsProcessAndThrows()
    {
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource();

        // A long sleep via ping so the process is still running when we cancel.
        var runTask = runner.RunAsync(
            new ProcessRunRequest(CmdExe, ["/c", "ping", "-n", "30", "127.0.0.1"]),
            cancellationToken: cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(250));

        var threw = false;
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Cancellation should surface as an OperationCanceledException");
    }

    [TestMethod]
    public async Task RunAsync_ExecutesBatchFileDirectly_AndForwardsArguments()
    {
        // Regression test for the concern that ProcessRunner (UseShellExecute=false) cannot execute a
        // .cmd/.bat target directly. FindAzureCli resolves 'az.cmd', which is handed to ProcessRunner
        // as the executable. Since .NET 8 the runtime transparently launches a .cmd/.bat target via
        // cmd.exe with safe argument escaping, so a batch file runs correctly and its arguments are
        // forwarded. This locks in that behavior so the cached-session token and 'az login' paths keep
        // working, mirroring how 'az.cmd' is invoked.
        var runner = new ProcessRunner();
        var batchFile = Path.Join(_tempDirectory.FullName, "harmless.cmd");

        // '@echo off' keeps the prompt out of stdout; the script echoes a marker plus its first arg.
        await File.WriteAllTextAsync(
            batchFile,
            "@echo off\r\necho batch-ran arg=%1\r\n",
            TestContext.CancellationToken);

        var result = await runner.RunAsync(
            new ProcessRunRequest(batchFile, ["forwarded-arg"]),
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, "A .cmd target must execute successfully via ProcessRunner");
        StringAssert.Contains(result.StandardOutput, "batch-ran");
        StringAssert.Contains(result.StandardOutput, "forwarded-arg",
            "Arguments must be forwarded to the batch file");
    }
}

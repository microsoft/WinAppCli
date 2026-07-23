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
}

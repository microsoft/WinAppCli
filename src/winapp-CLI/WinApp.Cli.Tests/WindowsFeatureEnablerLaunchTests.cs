// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// The real launch-and-wait path of <see cref="WindowsFeatureEnabler"/>, which the outcome-mapping
/// tests in <see cref="WindowsFeatureEnablerTests"/> replace wholesale with a stub launcher.
/// </summary>
/// <remarks>
/// No test here enables or disables a Windows feature. The launch is a parameter of
/// <see cref="WindowsFeatureEnabler.RunElevatedAsync(ProcessStartInfo, Func{ProcessStartInfo, Process?}, CancellationToken)"/>,
/// so each test supplies a harmless child of its own and the production wait, exit-code, and
/// handle-suppression logic runs against that instead of against <c>dism.exe</c>.
/// <para>
/// <c>[DoNotParallelize]</c>, for the same reason <c>SandboxHandleInheritanceTests</c> is: these
/// tests exercise <see cref="StandardHandleInheritance.Suppress"/>, which clears
/// <c>HANDLE_FLAG_INHERIT</c> on this <em>process's</em> standard handles and serializes on a
/// process-wide <see cref="Monitor"/>. Run in parallel, an open scope here would strip inheritance
/// from a child another test is starting at that moment, silently costing that child its output.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class WindowsFeatureEnablerLaunchTests
{
    /// <summary>Long enough that only a genuinely stranded gate fails it.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A harmless child that keeps running until the test ends it, so a wait on it must yield.
    /// </summary>
    /// <remarks>
    /// <c>ping</c> rather than <c>pause</c> or <c>timeout</c>: it neither reads the console nor
    /// fails when standard input is redirected, both of which a test host may have done.
    /// </remarks>
    private const string LongRunningHelper = "/c ping -n 60 127.0.0.1";

    public TestContext TestContext { get; set; } = null!;

    private static ProcessStartInfo HelperStartInfo(string arguments) => new()
    {
        FileName = Path.Join(Environment.SystemDirectory, "cmd.exe"),
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static Process? StartHelper(ProcessStartInfo startInfo) => Process.Start(startInfo);

    /// <summary>
    /// The handle-suppression scope must not be held across the wait for the elevated child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Suppression is serialized on a <see cref="Monitor"/>, and Monitor ownership belongs to a
    /// thread rather than to a logical call. A scope opened before <c>await WaitForExitAsync</c> and
    /// disposed after it is therefore disposed on whichever thread resumed the method, which throws
    /// <see cref="SynchronizationLockException"/> — and because the throw happens in the release
    /// path, it leaves the gate held by a thread that has already moved on. Every later suppressed
    /// launch in the process then blocks forever on <c>Monitor.Enter</c>: the Sandbox client and the
    /// guest agent both launch through one, so the next <c>--on sandbox</c> command hangs before it
    /// starts anything.
    /// </para>
    /// <para>
    /// The cross-thread resumption is forced, not hoped for. The launcher is entered from a
    /// dedicated thread, which is never a thread-pool thread, and <c>Join</c> returning proves that
    /// thread has terminated — so the resumption provably happened somewhere else. The child is
    /// asserted to be still running at that point, which is what proves the wait yielded at all.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task RunElevated_WhenTheWaitResumesOnAnotherThread_SucceedsAndLeavesTheGateFree()
    {
        Process? child = null;
        Task<int>? pending = null;

        var driver = new Thread(() => pending = WindowsFeatureEnabler.RunElevatedAsync(
            HelperStartInfo(LongRunningHelper),
            startInfo => child = StartHelper(startInfo),
            CancellationToken.None))
        {
            IsBackground = true,
        };

        driver.Start();
        driver.Join();

        Assert.IsNotNull(child, "The launch must have produced a child process.");
        Assert.IsNotNull(pending, "The launcher must have returned a pending wait.");

        Assert.IsFalse(
            child.HasExited,
            "The child must still be running here. Had it already exited, the wait could have completed " +
            "on the dedicated thread and this test would not be exercising a cross-thread resumption.");

        child.Kill(entireProcessTree: true);

        // The bug surfaces here: disposing a Monitor-owned scope on the resuming thread throws
        // SynchronizationLockException, which replaces the result of the wait.
        await pending.WaitAsync(GateTimeout, TestContext.CancellationToken);

        // And this is the damage that throw leaves behind. Acquired from a third thread, so a
        // stranded gate blocks here rather than being masked by Monitor's reentrancy.
        var reacquired = Task.Run(
            () =>
            {
                using var scope = StandardHandleInheritance.Suppress();
                return true;
            },
            TestContext.CancellationToken);

        Assert.IsTrue(
            await reacquired.WaitAsync(GateTimeout, TestContext.CancellationToken),
            "A later launch must still be able to suppress handle inheritance.");
    }

    /// <summary>The child's own exit code is what reaches the outcome mapping.</summary>
    /// <remarks>
    /// Elevation through <c>ShellExecuteEx</c> rules out capturing the child's output, so the exit
    /// code is the entire diagnosis. Returning the wrong one would misreport every servicing result.
    /// </remarks>
    [TestMethod]
    [DataRow(WindowsFeatureEnabler.ExitSuccess)]
    [DataRow(WindowsFeatureEnabler.ExitRestartRequired)]
    [DataRow(50)]
    public async Task RunElevated_ReturnsTheChildExitCode(int expected)
    {
        var exitCode = await WindowsFeatureEnabler.RunElevatedAsync(
            HelperStartInfo($"/c exit {expected}"), StartHelper, TestContext.CancellationToken);

        Assert.AreEqual(expected, exitCode);
    }

    /// <summary>
    /// A servicing pass that reaches the wait and succeeds is classified end to end.
    /// </summary>
    /// <remarks>
    /// The mapping is covered separately against a stub launcher; this proves the two halves are
    /// actually connected, so a real launch is not classified from a value that never left the stub.
    /// </remarks>
    [TestMethod]
    public async Task Enable_OverARealLaunch_ClassifiesTheChildExitCode()
    {
        var enabler = new WindowsFeatureEnabler
        {
            Launcher = (_, token) => WindowsFeatureEnabler.RunElevatedAsync(
                HelperStartInfo($"/c exit {WindowsFeatureEnabler.ExitRestartRequired}"), StartHelper, token),
        };

        var result = await enabler.EnableAsync(WindowsSandboxReadiness.FeatureName, TestContext.CancellationToken);

        Assert.AreEqual(FeatureEnableOutcome.RestartRequired, result.Outcome);
        Assert.AreEqual(WindowsFeatureEnabler.ExitRestartRequired, result.ExitCode);
    }

    /// <summary>Cancellation propagates rather than being reported as a servicing outcome.</summary>
    /// <remarks>
    /// A cancelled wait says nothing about whether the feature was enabled, so reporting it as
    /// <see cref="FeatureEnableOutcome.Failed"/> would assert something winapp does not know.
    /// </remarks>
    [TestMethod]
    public async Task RunElevated_WhenCancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        Process? started = null;

        var pending = WindowsFeatureEnabler.RunElevatedAsync(
            HelperStartInfo(LongRunningHelper),
            startInfo => started = StartHelper(startInfo),
            cancellation.Token);

        Assert.IsNotNull(started, "The child must have started before cancellation is exercised.");

        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

        try
        {
            started.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// A launch that produces no process fails with a message rather than a null dereference.
    /// </summary>
    [TestMethod]
    public async Task RunElevated_WhenWindowsStartsNothing_Throws()
    {
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => WindowsFeatureEnabler.RunElevatedAsync(
                HelperStartInfo("/c exit 0"), _ => null, TestContext.CancellationToken));

        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Message));
    }
}

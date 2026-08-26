// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Runs the host command channel against the real guest command server over an in-memory transport.
/// </summary>
/// <remarks>
/// Both halves of the protocol are exercised together, so a change that breaks their agreement —
/// a renamed message type, a different operation-identity encoding, a stream header change — fails
/// here rather than only inside a real Sandbox. Nothing in this file touches Windows Sandbox, which
/// is the structural point: if orchestration ever reached for a <c>wsb</c> command, these tests
/// could not run at all.
/// </remarks>
[TestClass]
public class GuestCommandServerTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private static readonly string[] InspectArguments = ["ui", "inspect"];

    private static GuestSessionInfo Interactive => new(SessionId: 1, "WinSta0", HasInputDesktop: true);

    private static GuestAgentIdentity Identity => new(
        Version: "9.9.9",
        BinaryHash: "abc123",
        Architecture: "arm64",
        ProtocolMinimum: GuestProtocol.MinimumVersion,
        ProtocolMaximum: GuestProtocol.CurrentVersion);

    private static GuestExecRequest Request(params string[] arguments) => new()
    {
        Executable = "winapp.exe",
        Arguments = [.. arguments],
    };

    [TestMethod]
    public async Task Capabilities_ReportsGuestArchitectureAndReadiness()
    {
        using var harness = new Harness(Interactive);

        var capabilities = await harness.Channel.GetCapabilitiesAsync(harness.Token);

        Assert.AreEqual("arm64", capabilities.Architecture);
        Assert.IsTrue(capabilities.SupportsRealInput);
        Assert.IsTrue(capabilities.SupportsScreenCapture);
        Assert.AreEqual(GuestOwnerContext.CooperativeUiTurnsVersion, capabilities.CooperativeUiTurnsVersion);

        // Windows Sandbox keeps nothing across teardown, which is why every new epoch must
        // reconcile deployments and runtimes from scratch.
        Assert.IsFalse(capabilities.PersistentStorage);
    }

    [TestMethod]
    public async Task Capabilities_DisconnectedClient_KeepsInspectionButRefusesInput()
    {
        // A closed Sandbox client leaves the guest session and UI Automation working while real
        // input and Windows Graphics Capture stop. Reporting input as available here would let a
        // command claim delivery for input that never arrives.
        using var harness = new Harness(Interactive with { HasInputDesktop = false });

        var capabilities = await harness.Channel.GetCapabilitiesAsync(harness.Token);

        Assert.IsTrue(capabilities.SupportsInteractiveDesktop);
        Assert.IsFalse(capabilities.SupportsRealInput);
        Assert.IsFalse(capabilities.SupportsScreenCapture);
    }

    [TestMethod]
    public async Task Execute_StreamsOutputInOrderAndReturnsExitCode()
    {
        using var harness = new Harness(Interactive);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = harness.Channel.ExecuteAsync(
            Request("ui", "inspect"),
            new GuestExecCallbacks(
                OnStarted: process => started.TrySetResult(process.ProcessId),
                OnStandardOutput: data => standardOutput.Append(Encoding.UTF8.GetString(data.Span)),
                OnStandardError: data => standardError.Append(Encoding.UTF8.GetString(data.Span))),
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var processId = await started.Task.WaitAsync(harness.Token);

        Assert.AreEqual(process.ProcessId, processId);
        CollectionAssert.AreEqual(InspectArguments, process.Request.Arguments);

        process.Emit(GuestStreamId.StandardOutput, "first ");
        process.Emit(GuestStreamId.StandardOutput, "second");
        process.Emit(GuestStreamId.StandardError, "warning");
        process.Exit(3);

        var result = await execution;

        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual(process.ProcessId, result.ProcessId);
        Assert.AreEqual("first second", standardOutput.ToString());
        Assert.AreEqual("warning", standardError.ToString());
    }

    [TestMethod]
    public async Task Execute_DetachedProcessReturnsAfterStartAndSurvivesTheChannel()
    {
        using var harness = new Harness(Interactive);

        var execution = harness.Channel.ExecuteAsync(
            new GuestExecRequest
            {
                Executable = "app.exe",
                Arguments = [],
                Detach = true,
            },
            callbacks: null,
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var result = await execution.WaitAsync(harness.Token);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(process.StopRequested);
        Assert.IsFalse(process.Disposed);

        process.Exit(17);
        Assert.IsTrue(
            SpinWait.SpinUntil(() => process.Disposed, TimeSpan.FromSeconds(1)),
            "The agent should release the detached process after it exits.");
    }

    [TestMethod]
    public async Task Execute_PreservesUnicodeAndArgumentBoundaries()
    {
        using var harness = new Harness(Interactive);

        // Arguments that would be mangled by any string-interpolated command line: embedded
        // quotes, spaces, and non-ASCII text.
        var arguments = new[] { "ui", "set-value", "a b\"c", "日本語 テキスト", string.Empty };

        var execution = harness.Channel.ExecuteAsync(
            new GuestExecRequest { Executable = "winapp.exe", Arguments = [.. arguments] },
            callbacks: null,
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        CollectionAssert.AreEqual(arguments, process.Request.Arguments);

        process.Exit(0);
        Assert.AreEqual(0, (await execution).ExitCode);
    }

    [TestMethod]
    public async Task StandardInput_IsForwardedAndClosed()
    {
        using var harness = new Harness(Interactive);

        var operationId = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = harness.Channel.ExecuteAsync(
            Request("ui", "record"),
            new GuestExecCallbacks(OnOperationId: id => operationId.TrySetResult(id)),
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var id = await operationId.Task.WaitAsync(harness.Token);

        await harness.Channel.SendStandardInputAsync(id, "hello"u8.ToArray(), harness.Token);
        await harness.Channel.SendStandardInputAsync(id, " world"u8.ToArray(), harness.Token);
        await harness.Channel.CloseStandardInputAsync(id, harness.Token);

        await WaitUntilAsync(() => process.StandardInputClosed, harness.Token);

        // Chunks must arrive whole and in order: a recording command that reads a newline to stop
        // would otherwise stop on a torn read.
        Assert.AreEqual(
            "hello world",
            string.Concat(process.StandardInput.Select(Encoding.UTF8.GetString)));

        process.Exit(0);
        await execution;

        Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task Cancellation_RequestsGracefulStopInTheGuest()
    {
        using var harness = new Harness(Interactive);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);

        var execution = harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, cancellation.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => execution);

        // Cancelling must actually reach the guest. Leaving the child running would strand a
        // process holding files the next deployment has to replace.
        await WaitUntilAsync(() => process.StopRequested, harness.Token);
    }

    [TestMethod]
    public async Task StaleEpoch_IsRefusedRatherThanApplied()
    {
        // The host believes it is talking to a generation the guest is not serving: exactly what a
        // command built before the Sandbox was recreated looks like.
        using var harness = new Harness(
            Interactive,
            hostEpoch: ExecutionTargetEpoch.Create("sandbox-1", "nonce-b"));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetStale, failure.Error.Code);
        Assert.IsTrue(harness.Processes.Started.IsEmpty, "A stale request must never start a process.");
    }

    [TestMethod]
    public async Task MatchingEpoch_IsAccepted()
    {
        using var harness = new Harness(Interactive);

        var execution = harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        process.Exit(0);

        Assert.AreEqual(0, (await execution).ExitCode);
    }

    [TestMethod]
    public async Task ProcessStartFailure_IsReportedAsStructuredFailure()
    {
        using var harness = new Harness(Interactive);

        harness.Processes.FailWith = new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.TransportFailed,
            Message = "The guest could not start 'winapp.exe'.",
        };

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task ServerShutdown_StopsRunningOperations()
    {
        using var harness = new Harness(Interactive);

        _ = harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);

        await harness.StopServerAsync();

        // Nothing the agent started may outlive the connection that asked for it.
        await WaitUntilAsync(() => process.StopRequested, CancellationToken.None);
    }

    // ---- Missing working directory --------------------------------------------------

    [TestMethod]
    public async Task Exec_WithAMissingWorkingDirectory_NamesTheDirectoryRatherThanBlamingTheExecutable()
    {
        using var harness = new Harness(Interactive);

        var missing = @"C:\WinApp\does-not-exist\anywhere";

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Channel.ExecuteAsync(
                new GuestExecRequest { Executable = "app.exe", Arguments = [], WorkingDirectory = missing },
                callbacks: null,
                harness.Token));

        StringAssert.Contains(failure.Error.Message, missing);
        Assert.IsTrue(harness.Processes.Started.IsEmpty, "A missing --cwd must be refused before anything starts.");
    }

    // ---- Stop before redeploy --------------------------------------------------------

    [TestMethod]
    public async Task StopPackage_ResolvesTheFamilyNameAndTerminatesTheCurrentFullName()
    {
        var launcher = new FakeAppLauncherService { FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc" };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        await harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", harness.Token);

        CollectionAssert.AreEqual(new[] { "Contoso.MyApp_1.0.0.0_x64__abc" }, launcher.StopPackageCalls);
    }

    [TestMethod]
    public async Task StopPackage_WhenNothingIsCurrentlyRegistered_SucceedsWithoutTerminatingAnything()
    {
        var launcher = new FakeAppLauncherService { FakePackageFullName = null };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        // Nothing registered under that family any more means nothing could be running under it.
        await harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", harness.Token);

        Assert.AreEqual(0, launcher.StopPackageCalls.Count);
    }

    [TestMethod]
    public async Task StopPackage_WhenTerminationCannotBeProven_FailsWithGuidanceNamingThePackage()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            StopPackageProcessesFailure = new InvalidOperationException("still running"),
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "Contoso.MyApp_abc");
    }

    [TestMethod]
    public async Task StopPackage_WithoutAConfiguredLauncher_FailsRatherThanSilentlyDoingNothing()
    {
        using var harness = new Harness(Interactive);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task StopProcess_WithAMatchingPidAndStartTime_Stops()
    {
        using var harness = new Harness(Interactive);

        harness.Server.StopTrackedProcessImpl = (pid, ticks) =>
        {
            Assert.AreEqual(4242, pid);
            Assert.AreEqual(555L, ticks);
            return GuestCommandServer.ProcessStopOutcome.Stopped;
        };

        // Must not throw.
        await harness.Channel.StopTrackedProcessAsync(4242, 555L, harness.Token);
    }

    [TestMethod]
    public async Task StopProcess_ThatIsAlreadyGone_SucceedsWithoutFailing()
    {
        using var harness = new Harness(Interactive);
        harness.Server.StopTrackedProcessImpl = (_, _) => GuestCommandServer.ProcessStopOutcome.AlreadyGone;

        await harness.Channel.StopTrackedProcessAsync(4242, 555L, harness.Token);
    }

    [TestMethod]
    public async Task StopProcess_WhenItCannotBeProvenStopped_FailsWithGuidanceNamingThePid()
    {
        using var harness = new Harness(Interactive);
        harness.Server.StopTrackedProcessImpl = (_, _) => GuestCommandServer.ProcessStopOutcome.Unproven;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.StopTrackedProcessAsync(4242, 555L, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "4242");
    }

    /// <summary>
    /// A stale or recycled PID must never be killed: a mismatched start time proves the tracked
    /// process is not the one currently holding that PID, however that came to be.
    /// </summary>
    [TestMethod]
    public void DefaultStopTrackedProcess_ARealProcessWithADifferentStartTime_IsNeverTouched()
    {
        using var helper = StartHelperProcess();
        var wrongStartTicksUtc = helper.StartTime.ToUniversalTime().Ticks - TimeSpan.FromDays(1).Ticks;

        var outcome = GuestCommandServer.DefaultStopTrackedProcess(helper.Id, wrongStartTicksUtc);

        Assert.AreEqual(GuestCommandServer.ProcessStopOutcome.AlreadyGone, outcome);
        Assert.IsFalse(helper.HasExited, "A process must never be touched when its start time does not match.");
    }

    [TestMethod]
    public void DefaultStopTrackedProcess_ARealProcessWithAMatchingStartTime_IsStopped()
    {
        using var helper = StartHelperProcess();
        var expectedStartTicksUtc = helper.StartTime.ToUniversalTime().Ticks;

        var outcome = GuestCommandServer.DefaultStopTrackedProcess(helper.Id, expectedStartTicksUtc);

        Assert.AreEqual(GuestCommandServer.ProcessStopOutcome.Stopped, outcome);
        Assert.IsTrue(helper.WaitForExit(5000));
    }

    [TestMethod]
    public void DefaultStopTrackedProcess_WhenNothingHasThatPid_ReportsAlreadyGone()
    {
        // No real process is expected to ever hold this PID during a test run.
        var outcome = GuestCommandServer.DefaultStopTrackedProcess(int.MaxValue - 5, expectedStartTicksUtc: 0);

        Assert.AreEqual(GuestCommandServer.ProcessStopOutcome.AlreadyGone, outcome);
    }

    private static System.Diagnostics.Process StartHelperProcess() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected condition was not reached in time.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>A connected host channel and guest server sharing one in-memory transport.</summary>
    private sealed class Harness : IAsyncDisposable, IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
        private readonly GuestCommandServer _server;
        private readonly Task _serverTask;

        public Harness(GuestSessionInfo session, ExecutionTargetEpoch? hostEpoch = null, IAppLauncherService? appLauncher = null)
        {
            var pair = new LoopbackTransportPair();
            Processes = new FakeGuestProcessHostFactory();

            _server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                Processes,
                new StaticGuestSessionProbe(session),
                Identity,
                files: null,
                guestWinapp: null,
                appLauncher);

            _serverTask = _server.RunAsync(_cancellation.Token);

            Channel = new GuestCommandChannel(pair.Host, hostEpoch ?? Epoch);
            Channel.Start();
        }

        public FakeGuestProcessHostFactory Processes { get; }

        public GuestCommandChannel Channel { get; }

        public GuestCommandServer Server => _server;

        public CancellationToken Token => _cancellation.Token;

        public async Task StopServerAsync()
        {
            await _cancellation.CancelAsync();

            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();

            // The server owns the guest transport and any process hosts still running, so it must
            // be disposed rather than merely cancelled.
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _cancellation.Dispose();
            await _server.DisposeAsync();
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Live coverage for taking over a Windows Sandbox winapp did not start, gated on
/// <c>WINAPP_SANDBOX_E2E=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// Take-over is the one behaviour that cannot be proven by a fake: the claim is that a guest with
/// somebody's work in it keeps that work, and only a real guest can be asked. So this test starts a
/// Sandbox the way a user would — directly through <c>wsb</c>, with no winapp state recording it —
/// leaves a file and a running process in it, then runs the real backend against it.
/// </para>
/// <para>
/// It stops only the instance it started itself, and it refuses to run at all if a Sandbox it did
/// not create is already up. Windows permits one at a time, and stopping somebody else's to make
/// room for a test would destroy exactly the thing the test exists to protect.
/// </para>
/// <para>
/// Nothing here enables a Windows feature or installs a package. Prerequisite setup is deliberately
/// untested live on a shared machine: it changes machine configuration, and proving it needs a
/// disposable host where Windows Sandbox has never been set up.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class SandboxLiveAdoptionTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Marker file left in the guest before winapp touches it.</summary>
    private const string GuestMarkerPath = @"C:\Windows\Temp\winapp-live-adoption-marker.txt";

    /// <summary>The instance this test started, if any. Only this one is ever stopped.</summary>
    private string? _startedByThisTest;

    private string? _redirectedStateRoot;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void RequireGate()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(SandboxLiveE2ETests.GateVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {SandboxLiveE2ETests.GateVariable}=1 on a machine with Windows Sandbox to run live "
                + "take-over coverage.");
        }

        if (Environment.GetEnvironmentVariable(SandboxLiveE2ETests.BinaryVariable) is not { Length: > 0 } binary ||
            !File.Exists(binary))
        {
            Assert.Inconclusive(
                $"Set {SandboxLiveE2ETests.BinaryVariable} to the architecture-matched NativeAOT winapp.exe "
                + "built for the guest.");
        }
    }

    [TestCleanup]
    public async Task CleanupOnlyWhatThisTestCreated()
    {
        if (_startedByThisTest is { Length: > 0 } instanceId)
        {
            try
            {
                await CreateCli().StopAsync(instanceId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is ExecutionTargetException or IOException)
            {
                Trace.TraceWarning("Could not stop the Sandbox this test created: {0}", ex.Message);
            }
        }

        if (_redirectedStateRoot is { Length: > 0 } root && Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not remove '{0}': {1}", root, ex.Message);
            }
        }
    }

    /// <summary>
    /// A Sandbox started by hand is taken over, keeps its contents, and is reused next time.
    /// </summary>
    [TestMethod]
    public async Task ManuallyStartedSandbox_IsAdoptedWithoutLosingWhatIsInIt()
    {
        await SkipUnlessTheMachineIsFreeAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var cli = CreateCli();

        // Started exactly as a user would, and deliberately not through winapp: no state record
        // exists for it, so winapp must treat it as an instance it did not create.
        var manualId = WindowsSandboxLifecycle.GenerateInstanceId();
        var reportedId = await cli.StartAsync(manualId, configuration: null, timeout.Token);
        _startedByThisTest = manualId;

        Assert.AreEqual(manualId, reportedId, "wsb start must honour the caller-assigned instance ID.");
        CollectionAssert.Contains(
            (await cli.ListAsync(timeout.Token)).ToArray(),
            manualId,
            "The instance the test started must be listed under the ID it asked for.");

        await WaitUntilResolvableAsync(cli, manualId, timeout.Token);
        await LeaveWorkInTheGuestAsync(cli, manualId, timeout.Token);

        // winapp gets its own state root, so this test never disturbs the machine's real one and
        // cannot accidentally inherit an ownership record for the instance it just started.
        _redirectedStateRoot = TestPaths.TempRoot(nameof(SandboxLiveAdoptionTests));
        Directory.CreateDirectory(_redirectedStateRoot);

        var provider = new TargetStateDirectoryProvider(_redirectedStateRoot);
        var stateStore = new TargetStateStore(provider);

        var backend = CreateBackend(cli, provider, stateStore);
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        await using (var adopted = await orchestrator.PrepareAsync(PrepareTargetOptions.Mutating, timeout.Token))
        {
            adopted.ReleaseMutationLease();

            Assert.IsFalse(
                adopted.Reused,
                "A guest winapp did not start has nothing prepared under this epoch, so it is not a warm reuse.");

            var diagnostics = backend.DescribeForDiagnostics();
            Assert.AreEqual(manualId, diagnostics["sandboxId"], "winapp must take over the running instance.");
            Assert.AreEqual("true", diagnostics["sandboxAdopted"]);

            var persisted = stateStore.Read(ExecutionTargetRef.WindowsSandboxDefault);
            Assert.AreEqual(manualId, persisted!.InstanceId);
            Assert.AreEqual(nameof(SandboxInstanceOrigin.Adopted), persisted.InstanceOrigin);
        }

        // The whole point: the guest still has what it had before winapp arrived.
        Assert.AreEqual(
            0,
            await cli.ExecuteAsync(
                manualId,
                $@"cmd.exe /c if exist {GuestMarkerPath} (exit 0) else (exit 3)",
                workingDirectory: null,
                asSystem: false,
                timeout.Token),
            "Taking over a Sandbox must not remove files that were already in it.");

        Assert.AreEqual(
            0,
            await cli.ExecuteAsync(
                manualId,
                "powershell.exe -NoProfile -NonInteractive -Command "
                + "\"if (Get-Process -Name ping -ErrorAction SilentlyContinue) { exit 0 } else { exit 3 }\"",
                workingDirectory: null,
                asSystem: false,
                timeout.Token),
            "Taking over a Sandbox must not stop processes that were already running in it.");

        CollectionAssert.Contains(
            (await cli.ListAsync(timeout.Token)).ToArray(),
            manualId,
            "winapp must never stop a Sandbox it took over.");

        // A second process -- a fresh backend over the same state root -- must now find an instance
        // winapp owns and reuse it, rather than taking it over again under a new epoch.
        var second = CreateBackend(cli, provider, stateStore);
        var secondOrchestrator = new ExecutionTargetOrchestrator(
            second,
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        await using var reused = await secondOrchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, timeout.Token);

        Assert.IsTrue(reused.Reused, "The next command must reuse the instance winapp now owns.");
        Assert.AreEqual(manualId, second.DescribeForDiagnostics()["sandboxId"]);
    }

    /// <summary>
    /// <c>wsb start --id</c> honours the caller's GUID, which is what makes recovery possible.
    /// </summary>
    /// <remarks>
    /// The whole partial-start recovery design rests on this: winapp writes down an ID before
    /// starting, and reconciles that exact ID afterwards. If <c>wsb</c> ever stopped honouring it,
    /// recovery would silently degrade into guessing from a list, so it is verified against the real
    /// tool rather than assumed.
    /// </remarks>
    [TestMethod]
    public async Task WsbStart_HonoursTheCallerAssignedInstanceId()
    {
        await SkipUnlessTheMachineIsFreeAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var cli = CreateCli();
        var assignedId = WindowsSandboxLifecycle.GenerateInstanceId();

        var reportedId = await cli.StartAsync(assignedId, configuration: null, timeout.Token);
        _startedByThisTest = assignedId;

        Assert.AreEqual(assignedId, reportedId);

        await WaitUntilResolvableAsync(cli, assignedId, timeout.Token);

        Assert.IsTrue(
            await cli.IsResolvableAsync(assignedId, timeout.Token),
            "An instance winapp claims must be reachable before anything is prepared in it.");
    }

    /// <summary>Puts a file and a running process into the guest before winapp sees it.</summary>
    private static async Task LeaveWorkInTheGuestAsync(
        IWindowsSandboxCli cli,
        string instanceId,
        CancellationToken cancellationToken)
    {
        await cli.ExecuteAsync(
            instanceId,
            $@"cmd.exe /c echo pre-existing work > {GuestMarkerPath}",
            workingDirectory: null,
            asSystem: false,
            cancellationToken);

        await cli.ExecuteAsync(
            instanceId,
            @"cmd.exe /c start """" /min ping.exe -t 127.0.0.1",
            workingDirectory: null,
            asSystem: false,
            cancellationToken);
    }

    private static async Task WaitUntilResolvableAsync(
        IWindowsSandboxCli cli,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);

        while (!await cli.IsResolvableAsync(instanceId, cancellationToken))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Inconclusive("The Sandbox this test started never became reachable.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>
    /// Skips unless this machine can run Sandbox and no instance is already up.
    /// </summary>
    /// <remarks>
    /// A Sandbox that is already running is not a test failure. Windows allows one at a time, and
    /// this test would have to stop it to run — which is precisely the destructive act the feature
    /// is designed never to perform.
    /// </remarks>
    private async Task SkipUnlessTheMachineIsFreeAsync()
    {
        var cli = CreateCli();

        if (!cli.IsAvailable)
        {
            Assert.Inconclusive("Windows Sandbox is not set up on this machine.");
        }

        if ((await cli.ListAsync(TestContext.CancellationToken)).Count > 0)
        {
            Assert.Inconclusive(
                "A Windows Sandbox instance is already running. Windows permits only one, and this test "
                + "will not stop an instance it did not create.");
        }
    }

    private static WindowsSandboxCli CreateCli() => new(new ProcessRunner());

    private static WindowsSandboxBackend CreateBackend(
        IWindowsSandboxCli cli,
        ITargetStateDirectoryProvider provider,
        ITargetStateStore stateStore) =>
        new(
            cli,
            new WindowsSandboxLifecycle(cli, stateStore),
            provider,
            new FixedHostBinaryProvider(
                new FileInfo(Environment.GetEnvironmentVariable(SandboxLiveE2ETests.BinaryVariable)!)),
            new WindowsSandboxWindowController(),

            // No setup runner: this test must never change the machine's feature or package state.
            setup: null,
            stateStore);

    private sealed class FixedHostBinaryProvider(FileInfo binary) : IHostWinappBinaryProvider
    {
        public FileInfo GetBinary() => binary;
    }
}

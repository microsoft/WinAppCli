// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the single entry point every <c>--sandbox</c> command goes through.
/// </summary>
/// <remarks>
/// The ordering rules are the contract, and each is a failure mode rather than a preference:
/// probing before mutation is what keeps an unsupported host from failing only after a long build;
/// locking only for mutation is what keeps a read-only inspection from blocking behind a
/// deployment; and never falling back to local execution is what keeps <c>--sandbox</c> from
/// silently running an application on the user's own desktop.
/// </remarks>
[TestClass]
public class ExecutionTargetOrchestratorTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Prepare_UnsupportedHost_FailsBeforeTouchingTheTarget()
    {
        var backend = new FakeBackend { Support = TargetSupportResult.Unsupported(new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.Unsupported,
            Message = "Windows Sandbox is not installed.",
        }) };

        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            new FakeMutationLock(),
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Mutating, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, failure.Error.Code);

        // Nothing was started, and nothing fell back to running locally.
        Assert.AreEqual(0, backend.EnsureCalls);
    }

    [TestMethod]
    public async Task Prepare_MutatingCommand_TakesTheLock()
    {
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.Mutating, TestContext.CancellationToken);

        Assert.AreEqual(1, mutationLock.AcquireCalls);

        // Released once the target is prepared, not held for the life of a running application --
        // otherwise one long-running app would block every other workflow.
        Assert.AreEqual(1, mutationLock.ReleaseCalls);
    }

    [TestMethod]
    public async Task Prepare_ReadOnlyCommand_DoesNotTakeTheLock()
    {
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        // Read-only UI Automation is explicitly outside the lock's scope, so an inspection never
        // waits behind a deployment and never makes one wait.
        Assert.AreEqual(0, mutationLock.AcquireCalls);
    }

    [TestMethod]
    public async Task Prepare_HoldsTheConnectionLeaseForThePreparedChannelLifetime()
    {
        var connectionLock = new FakeConnectionLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            new FakeMutationLock(),
            connectionLock);

        var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly,
            TestContext.CancellationToken);

        Assert.AreEqual(1, connectionLock.AcquireCalls);
        Assert.AreEqual(0, connectionLock.ReleaseCalls);

        await prepared.DisposeAsync();

        Assert.AreEqual(1, connectionLock.ReleaseCalls);
    }

    [TestMethod]
    public async Task Prepare_LockHeldByAnotherProcess_FailsWithGuidance()
    {
        using var mutationLock = new FakeMutationLock { Available = false };
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Mutating, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
        Assert.IsNotNull(failure.Error.UserAction);
    }

    [TestMethod]
    public async Task Prepare_DisconnectedClient_RefusesForegroundWork()
    {
        using var mutationLock = new FakeMutationLock();
        var backend = new FakeBackend { SupportsInteractiveDesktop = false };
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            mutationLock,
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Interactive, TestContext.CancellationToken));

        // A closed Sandbox client still reports an interactive desktop, because UI Automation keeps
        // working -- what stops is real input and screen capture. So the failure is "input not
        // ready", not "no interactive session", and gating on the wrong capability here would admit
        // commands that then report input they never delivered.
        Assert.AreEqual(ExecutionTargetErrorCodes.InputNotReady, failure.Error.Code);

        // Reconnecting changes what is on screen, so it is offered as advisory guidance rather than
        // performed automatically.
        Assert.IsTrue(failure.Error.NextCommand?.Advisory);
    }

    [TestMethod]
    public async Task Prepare_Session0_RefusesForegroundWorkAsNoInteractiveSession()
    {
        using var mutationLock = new FakeMutationLock();
        var backend = new FakeBackend { SessionId = 0 };
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            mutationLock,
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Interactive, TestContext.CancellationToken));

        // Session 0 has no desktop at all, which is a different failure from a disconnected client
        // and needs a different recovery.
        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, failure.Error.Code);
    }

    [TestMethod]
    public async Task Prepare_DisconnectedClient_StillAllowsReadOnlyWork()
    {
        using var mutationLock = new FakeMutationLock();
        var backend = new FakeBackend { SupportsInteractiveDesktop = false };
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            mutationLock,
            new FakeConnectionLock());

        // A disconnected client leaves UI Automation working, so inspection must stay available in
        // exactly the state where input has to be refused.
        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        Assert.AreEqual(Epoch, prepared.Epoch);
        Assert.IsTrue(prepared.Capabilities.SupportsInteractiveDesktop);
        Assert.IsFalse(prepared.Capabilities.SupportsRealInput);
    }

    [TestMethod]
    public async Task Prepare_ReusedInstance_ReportsItInProgress()
    {
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend { Reused = true },
            new FakeMutationLock(),
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        Assert.IsTrue(prepared.Reused);
        Assert.AreEqual("Reusing Windows Sandbox...", ExecutionTargetOrchestrator.DescribeProgress(reused: true));
        Assert.AreEqual("Preparing Windows Sandbox...", ExecutionTargetOrchestrator.DescribeProgress(reused: false));
    }

    /// <summary>A backend whose responses are scripted, standing in for Windows Sandbox.</summary>
    private sealed class FakeBackend : IExecutionTargetBackend
    {
        private readonly List<GuestCommandServer> _servers = [];

        public ExecutionTargetRef Target => ExecutionTargetRef.WindowsSandboxDefault;

        public TargetSupportResult Support { get; init; } = TargetSupportResult.Supported;

        public bool SupportsInteractiveDesktop { get; init; } = true;

        public int SessionId { get; init; } = 1;

        public bool Reused { get; init; }

        public int EnsureCalls { get; private set; }

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Support);

        public Task<TargetConnection> EnsureConnectedAsync(
            EnsureTargetOptions options,
            CancellationToken cancellationToken)
        {
            EnsureCalls++;

            var pair = new LoopbackTransportPair();

            // A real guest server rather than a scripted peer, so capability negotiation exercises
            // the same code path the orchestrator will meet in production.
            var server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                new FakeGuestProcessHostFactory(),
                new StaticGuestSessionProbe(new GuestSessionInfo(
                    SessionId,
                    "WinSta0",
                    HasInputDesktop: SupportsInteractiveDesktop)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1));

            _servers.Add(server);
            _ = server.RunAsync(CancellationToken.None);

            return Task.FromResult(new TargetConnection(Epoch, pair.Host, Reused));
        }

        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() =>
            new Dictionary<string, string> { ["sandboxId"] = "sandbox-1" };
    }

    /// <summary>A lock that records use and can pretend another process holds it.</summary>
    private sealed class FakeMutationLock : ITargetMutationLock, IDisposable
    {
        private readonly List<FileStream> _streams = [];

        public bool Available { get; init; } = true;

        public int AcquireCalls { get; private set; }

        /// <summary>
        /// How many leases have been released, inferred from their handles being closed.
        /// </summary>
        /// <remarks>
        /// Observed through the handle rather than a callback because that is what actually
        /// releases the lock for other processes — a counter could be incremented while the handle
        /// stayed open, which is precisely the bug this assertion exists to catch.
        /// </remarks>
        public int ReleaseCalls => _streams.Count(s => !s.CanRead);

        public TargetMutationLease? TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;

            if (!Available)
            {
                return null;
            }

            var path = TestPaths.TempFile("mutation-lock", ".lock");
            var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            _streams.Add(stream);

            return new TargetMutationLease(stream, wasAbandoned: false);
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }
    }

    private sealed class FakeConnectionLock : ITargetConnectionLock
    {
        private readonly List<FileStream> _streams = [];

        public int AcquireCalls { get; private set; }

        public int ReleaseCalls => _streams.Count(stream => !stream.CanRead);

        public TargetConnectionLease? TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            var path = TestPaths.TempFile("connection-lock", ".lock");
            var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            _streams.Add(stream);
            return new TargetConnectionLease(stream);
        }
    }
}

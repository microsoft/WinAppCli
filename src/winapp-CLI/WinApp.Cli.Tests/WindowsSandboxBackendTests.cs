// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowsSandboxBackendTests
{
    private const string SandboxOne = "11111111-1111-1111-1111-111111111111";
    private const string SandboxTwo = "22222222-2222-2222-2222-222222222222";
    private const string EpochOne = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public async Task ProbeAsync_UnsupportedHostDoesNotInvokeWsb()
    {
        var fixture = new Fixture { IsSupportedHost = false };

        var result = await fixture.Backend.ProbeAsync();

        Assert.IsFalse(result.IsSupported);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.UnsupportedHost, result.Diagnostics.Single().Code);
        Assert.AreEqual(0, fixture.Cli.ListCount);
    }

    [TestMethod]
    public async Task ProbeAsync_MissingCliReturnsSpecificDiagnostic()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(FailedList(WindowsSandboxCliFailure.ExecutableMissing, "missing"));

        var result = await fixture.Backend.ProbeAsync();

        Assert.IsFalse(result.IsSupported);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxCliMissing, result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task EnsureAsync_WarmReusePreservesEpochAndDoesNotMutate()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxOne));
        fixture.State.ReadResult = ValidState(SandboxOne, EpochOne, 4);

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(EpochOne, result.Instance!.Epoch.Value);
        Assert.AreEqual(0, fixture.Cli.StartCount);
        Assert.AreEqual(0, fixture.Cli.StopIds.Count);
        Assert.AreEqual(0, fixture.State.Writes.Count);
    }

    [TestMethod]
    public async Task GetStatusAsync_ReconcilesOwnedAndUnmanagedInstances()
    {
        var owned = new Fixture();
        owned.Cli.ListResults.Enqueue(SuccessfulList(SandboxOne));
        owned.State.ReadResult = ValidState(SandboxOne, EpochOne, 2);
        var unmanaged = new Fixture();
        unmanaged.Cli.ListResults.Enqueue(SuccessfulList(SandboxTwo));

        var ownedResult = await owned.Backend.GetStatusAsync();
        var unmanagedResult = await unmanaged.Backend.GetStatusAsync();

        Assert.AreEqual(ExecutionTargetStatus.Running, ownedResult.Status);
        Assert.AreEqual(EpochOne, ownedResult.Epoch!.Value.Value);
        Assert.AreEqual(ExecutionTargetStatus.Unmanaged, unmanagedResult.Status);
        Assert.AreEqual(
            ExecutionTargetDiagnosticCode.WindowsSandboxUnmanagedInstance,
            unmanagedResult.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task EnsureAsync_UnmanagedInstanceIsNeverAdoptedOrStopped()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxOne));
        fixture.State.ReadResult = new WindowsSandboxStateReadResult(
            WindowsSandboxStateReadStatus.Missing,
            null);

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        var diagnostic = result.Diagnostics.Single();
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxUnmanagedInstance, diagnostic.Code);
        Assert.AreEqual($"wsb stop --id {SandboxOne}", diagnostic.RecoveryCommand);
        Assert.AreEqual(0, fixture.Cli.StartCount);
        Assert.AreEqual(0, fixture.Cli.StopIds.Count);
    }

    [TestMethod]
    public async Task EnsureAsync_ExternalStopCreatesNewEpochAndIncrementsRevision()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxTwo));
        fixture.Cli.StartResults.Enqueue(WindowsSandboxCliResult<string>.Success(SandboxTwo));
        fixture.State.ReadResult = ValidState(SandboxOne, EpochOne, 4);

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(SandboxTwo, result.Instance!.ProviderInstanceId);
        Assert.AreNotEqual(EpochOne, result.Instance.Epoch.Value);
        Assert.AreEqual(5, fixture.State.Writes.Single().Revision);
        Assert.AreEqual(SandboxTwo, fixture.State.Writes.Single().ProviderInstanceId);
    }

    [TestMethod]
    public async Task EnsureAsync_StateCommitFailureRollsBackOnlyStartedInstance()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxOne));
        fixture.Cli.StartResults.Enqueue(WindowsSandboxCliResult<string>.Success(SandboxOne));
        fixture.State.WriteException = new IOException("disk full");

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxStateUnavailable, result.Diagnostics.Single().Code);
        CollectionAssert.AreEqual(new[] { SandboxOne }, fixture.Cli.StopIds);
    }

    [TestMethod]
    public async Task EnsureAsync_StateCommitCancellationRollsBackOnceAndPropagates()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxOne));
        fixture.Cli.StartResults.Enqueue(WindowsSandboxCliResult<string>.Success(SandboxOne));
        fixture.State.WriteException = new OperationCanceledException();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => fixture.Backend.EnsureAsync(new ExecutionTargetRequirements()));

        CollectionAssert.AreEqual(new[] { SandboxOne }, fixture.Cli.StopIds);
    }

    [TestMethod]
    public async Task EnsureAsync_StartCancellationDoesNotMutateUnprovenInstance()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.StartException = new OperationCanceledException();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => fixture.Backend.EnsureAsync(new ExecutionTargetRequirements()));

        Assert.AreEqual(1, fixture.Cli.ListCount);
        Assert.AreEqual(0, fixture.Cli.StopIds.Count);
    }

    [TestMethod]
    public async Task EnsureAsync_ConfirmationMismatchRollsBackOnlyStartedInstance()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxTwo));
        fixture.Cli.StartResults.Enqueue(WindowsSandboxCliResult<string>.Success(SandboxOne));

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxStartFailed, result.Diagnostics.Single().Code);
        CollectionAssert.AreEqual(new[] { SandboxOne }, fixture.Cli.StopIds);
        Assert.AreEqual(0, fixture.State.Writes.Count);
    }

    [TestMethod]
    public async Task EnsureAsync_RollbackFailureIsExplicitlyDiagnosed()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxTwo));
        fixture.Cli.StartResults.Enqueue(WindowsSandboxCliResult<string>.Success(SandboxOne));
        fixture.Cli.StopResult = WindowsSandboxCliResult<bool>.Failed(
            WindowsSandboxCliFailure.CommandFailed,
            "stop failed");

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.AreEqual(2, result.Diagnostics.Count);
        Assert.AreEqual(
            ExecutionTargetDiagnosticCode.WindowsSandboxRollbackFailed,
            result.Diagnostics[1].Code);
        Assert.AreEqual($"wsb stop --id {SandboxOne}", result.Diagnostics[1].RecoveryCommand);
    }

    [TestMethod]
    public async Task EnsureAsync_UnparseableStartDoesNotMutateUnprovenSingleton()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.StartResults.Enqueue(WindowsSandboxCliResult<string>.Failed(
            WindowsSandboxCliFailure.IncompatibleOutput,
            "bad json"));

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxCliIncompatible, result.Diagnostics.Single().Code);
        Assert.AreEqual(0, fixture.Cli.StopIds.Count);
    }

    [TestMethod]
    public async Task EnsureAsync_CorruptStateWithLiveInstanceFailsClosed()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxOne));
        fixture.State.ReadResult = new WindowsSandboxStateReadResult(
            WindowsSandboxStateReadStatus.Corrupt,
            null,
            "corrupt");

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxStateUnavailable, result.Diagnostics.Single().Code);
        Assert.AreEqual(0, fixture.Cli.StopIds.Count);
    }

    [TestMethod]
    public async Task EnsureAsync_CorruptStateWithoutLiveInstanceFailsClosed()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.State.ReadResult = new WindowsSandboxStateReadResult(
            WindowsSandboxStateReadStatus.Corrupt,
            null,
            "corrupt");

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxStateUnavailable, result.Diagnostics.Single().Code);
        Assert.AreEqual(0, fixture.Cli.StartCount);
        Assert.AreEqual(0, fixture.State.Writes.Count);
    }

    [TestMethod]
    public async Task GetStatusAsync_CorruptStateWithoutLiveInstanceIsUnavailable()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.State.ReadResult = new WindowsSandboxStateReadResult(
            WindowsSandboxStateReadStatus.Corrupt,
            null,
            "corrupt");

        var result = await fixture.Backend.GetStatusAsync();

        Assert.AreEqual(ExecutionTargetStatus.Unavailable, result.Status);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxStateUnavailable, result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task EnsureAsync_ExhaustedRevisionFailsBeforeStartingSandbox()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.State.ReadResult = ValidState(SandboxOne, EpochOne, long.MaxValue);

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.WindowsSandboxStateUnavailable, result.Diagnostics.Single().Code);
        Assert.AreEqual(0, fixture.Cli.StartCount);
        Assert.AreEqual(0, fixture.State.Writes.Count);
    }

    [TestMethod]
    public async Task EnsureAsync_PenultimateRevisionAdvancesWithoutRollback()
    {
        var fixture = new Fixture();
        fixture.Cli.ListResults.Enqueue(SuccessfulList());
        fixture.Cli.ListResults.Enqueue(SuccessfulList(SandboxTwo));
        fixture.Cli.StartResults.Enqueue(WindowsSandboxCliResult<string>.Success(SandboxTwo));
        fixture.State.ReadResult = ValidState(SandboxOne, EpochOne, long.MaxValue - 1);

        var result = await fixture.Backend.EnsureAsync(new ExecutionTargetRequirements());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(long.MaxValue, fixture.State.Writes.Single().Revision);
        Assert.AreEqual(0, fixture.Cli.StopIds.Count);
    }

    [TestMethod]
    public async Task EnsureAsync_InteractiveRequirementFailsBeforeMutation()
    {
        var fixture = new Fixture();

        var result = await fixture.Backend.EnsureAsync(
            new ExecutionTargetRequirements(InteractiveDesktop: true));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.CapabilityUnavailable, result.Diagnostics.Single().Code);
        Assert.AreEqual(0, fixture.Cli.ListCount);
    }

    [TestMethod]
    public async Task EnsureAsync_ConcurrentWarmReuseIsSerializedByMutationLock()
    {
        var cli = new ConcurrentDetectingCli(SandboxOne);
        var state = new FakeStateStore
        {
            ReadResult = ValidState(SandboxOne, EpochOne, 1),
        };
        var mutex = new WindowsSandboxMutationLock(
            @"Local\WinApp.Cli.Tests." + Guid.NewGuid().ToString("N"));
        var backend = new WindowsSandboxBackend(new FakeHost(true), cli, state, mutex);

        var first = backend.EnsureAsync(new ExecutionTargetRequirements());
        var second = backend.EnsureAsync(new ExecutionTargetRequirements());
        await Task.WhenAll(first, second);

        Assert.IsTrue(first.Result.Succeeded);
        Assert.IsTrue(second.Result.Succeeded);
        Assert.AreEqual(1, cli.MaxConcurrentLists);
    }

    private static WindowsSandboxCliResult<IReadOnlyList<string>> SuccessfulList(params string[] ids) =>
        WindowsSandboxCliResult<IReadOnlyList<string>>.Success(ids);

    private static WindowsSandboxCliResult<IReadOnlyList<string>> FailedList(
        WindowsSandboxCliFailure failure,
        string error) =>
        WindowsSandboxCliResult<IReadOnlyList<string>>.Failed(failure, error);

    private static WindowsSandboxStateReadResult ValidState(
        string instanceId,
        string epoch,
        long revision) =>
        new(
            WindowsSandboxStateReadStatus.Valid,
            new WindowsSandboxTargetState
            {
                Schema = WindowsSandboxTargetState.CurrentSchema,
                TargetId = ExecutionTargetRef.WindowsSandboxDefaultId,
                ProviderInstanceId = instanceId,
                Epoch = epoch,
                Revision = revision,
                CreatedAtUtc = "2026-08-19T12:00:00Z",
            });

    private sealed class Fixture
    {
        public bool IsSupportedHost { get; set; } = true;

        public FakeCli Cli { get; } = new();

        public FakeStateStore State { get; } = new();

        public WindowsSandboxBackend Backend =>
            new(new FakeHost(IsSupportedHost), Cli, State, new ImmediateMutationLock());
    }

    private sealed class FakeHost(bool isSupported) : IWindowsSandboxHost
    {
        public bool IsSupportedOperatingSystem => isSupported;
    }

    private sealed class FakeCli : IWindowsSandboxCli
    {
        public Queue<WindowsSandboxCliResult<IReadOnlyList<string>>> ListResults { get; } = new();

        public Queue<WindowsSandboxCliResult<string>> StartResults { get; } = new();

        public List<string> StopIds { get; } = [];

        public int ListCount { get; private set; }

        public int StartCount { get; private set; }

        public Exception? StartException { get; set; }

        public WindowsSandboxCliResult<bool> StopResult { get; set; } =
            WindowsSandboxCliResult<bool>.Success(true);

        public Task<WindowsSandboxCliResult<IReadOnlyList<string>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            ListCount++;
            return Task.FromResult(ListResults.Dequeue());
        }

        public Task<WindowsSandboxCliResult<string>> StartAsync(
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (StartException is not null)
            {
                return Task.FromException<WindowsSandboxCliResult<string>>(StartException);
            }
            return Task.FromResult(StartResults.Dequeue());
        }

        public Task<WindowsSandboxCliResult<bool>> StopAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            StopIds.Add(instanceId);
            return Task.FromResult(StopResult);
        }
    }

    private sealed class ConcurrentDetectingCli(string instanceId) : IWindowsSandboxCli
    {
        private int _activeLists;
        private int _maxConcurrentLists;

        public int MaxConcurrentLists => _maxConcurrentLists;

        public Task<WindowsSandboxCliResult<IReadOnlyList<string>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeLists);
            var currentMax = Volatile.Read(ref _maxConcurrentLists);
            while (active > currentMax)
            {
                currentMax = Interlocked.CompareExchange(ref _maxConcurrentLists, active, currentMax);
            }
            Thread.Sleep(100);
            Interlocked.Decrement(ref _activeLists);
            return Task.FromResult(SuccessfulList(instanceId));
        }

        public Task<WindowsSandboxCliResult<string>> StartAsync(
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Warm reuse must not start a sandbox.");

        public Task<WindowsSandboxCliResult<bool>> StopAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Warm reuse must not stop a sandbox.");
    }

    private sealed class FakeStateStore : IWindowsSandboxStateStore
    {
        public WindowsSandboxStateReadResult ReadResult { get; set; } =
            new(WindowsSandboxStateReadStatus.Missing, null);

        public Exception? WriteException { get; set; }

        public List<WindowsSandboxTargetState> Writes { get; } = [];

        public FileInfo GetStateFile() => new("unused");

        public Task<WindowsSandboxStateReadResult> ReadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadResult);

        public Task WriteAsync(
            WindowsSandboxTargetState state,
            CancellationToken cancellationToken = default)
        {
            if (WriteException is not null)
            {
                return Task.FromException(WriteException);
            }
            Writes.Add(state);
            ReadResult = new WindowsSandboxStateReadResult(WindowsSandboxStateReadStatus.Valid, state);
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateMutationLock : IWindowsSandboxMutationLock
    {
        public IDisposable Acquire(CancellationToken cancellationToken = default) => new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Deterministic stand-in for <c>wsb.exe</c>. Windows permits only one Sandbox and starting one is
/// slow and disruptive, so the singleton and ownership rules are verified here instead of against a
/// real instance.
/// </summary>
internal sealed class FakeWindowsSandboxCli : IWindowsSandboxCli
{
    private readonly List<string> _running = [];

    public bool IsAvailable { get; set; } = true;

    /// <summary>IDs <see cref="StartAsync"/> hands out, in order.</summary>
    public Queue<string> StartIds { get; } = new();

    /// <summary>Every instance ID <see cref="StopAsync"/> was called with.</summary>
    public List<string> Stopped { get; } = [];

    /// <summary>How many times <see cref="StartAsync"/> was called.</summary>
    public int StartCount { get; private set; }

    /// <summary>Invoked before each <see cref="ListAsync"/>, to simulate teardown completing.</summary>
    public Action? OnList { get; set; }

    public void SetRunning(params string[] ids)
    {
        _running.Clear();
        _running.AddRange(ids);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        OnList?.Invoke();
        return Task.FromResult<IReadOnlyList<string>>([.. _running]);
    }

    public Task<string> StartAsync(string? configuration, CancellationToken cancellationToken)
    {
        StartCount++;
        var id = StartIds.Count > 0 ? StartIds.Dequeue() : $"instance-{StartCount}";
        _running.Add(id);
        return Task.FromResult(id);
    }

    public Task StopAsync(string id, CancellationToken cancellationToken)
    {
        Stopped.Add(id);
        _running.Remove(id);
        return Task.CompletedTask;
    }

    public Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult("172.27.0.2");

    public Task ShareFolderAsync(
        string id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ConnectAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<int> ExecuteAsync(
        string id,
        string command,
        string? workingDirectory,
        bool asSystem,
        CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>
/// Tests for <see cref="WindowsSandboxLifecycle"/>: singleton ownership, refusal to touch anything
/// winapp did not create, warm reuse, external termination, and teardown waiting.
/// </summary>
[TestClass]
public class WindowsSandboxLifecycleTests
{
    private DirectoryInfo _tempRoot = null!;
    private FakeWindowsSandboxCli _cli = null!;
    private TargetStateStore _stateStore = null!;
    private WindowsSandboxLifecycle _lifecycle = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"SandboxLifecycle_{Guid.NewGuid():N}"));
        _tempRoot.Create();

        _cli = new FakeWindowsSandboxCli();
        _stateStore = new TargetStateStore(new TargetStateDirectoryProvider(_tempRoot.FullName));
        _lifecycle = new WindowsSandboxLifecycle(_cli, _stateStore);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempRoot.Exists)
        {
            _tempRoot.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Reconcile_NoStateAndNothingRunning_ReportsTerminated()
    {
        var result = await _lifecycle.ReconcileAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(TargetLifecycleState.Terminated, result.State);
        Assert.IsNull(result.InstanceId);
        Assert.IsTrue(result.Epoch.IsNone);
    }

    [TestMethod]
    public async Task EnsureInstance_ColdStart_CreatesAndPersistsOwnership()
    {
        _cli.StartIds.Enqueue("sandbox-a");

        var lease = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-a", lease.InstanceId);
        Assert.IsFalse(lease.Reused);
        Assert.IsFalse(lease.Epoch.IsNone);

        var persisted = _stateStore.Read(ExecutionTargetRef.WindowsSandboxDefault);
        Assert.AreEqual("sandbox-a", persisted!.InstanceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.BootNonce), "A boot nonce is required to form an epoch.");
    }

    [TestMethod]
    public async Task EnsureInstance_WarmReuse_DoesNotStartASecondSandbox()
    {
        _cli.StartIds.Enqueue("sandbox-a");
        var first = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        var second = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(second.Reused, "A healthy managed Sandbox must be reused, not recreated.");
        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreEqual(first.Epoch, second.Epoch, "Reuse must preserve the epoch so live handles stay valid.");
        Assert.AreEqual(1, _cli.StartCount);
    }

    [TestMethod]
    public async Task EnsureInstance_UnmanagedSandboxRunning_RefusesWithAdvisoryGuidance()
    {
        _cli.SetRunning("someone-elses-sandbox");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.UnmanagedInstance, failure.Error.Code);
        Assert.AreEqual("someone-elses-sandbox", failure.Error.Context!["sandboxId"]);
        Assert.AreEqual("wsb stop --id someone-elses-sandbox", failure.Error.NextCommand!.Command);

        // The instance may hold the user's work, so stopping it needs a human decision.
        Assert.IsTrue(failure.Error.NextCommand.Advisory, "Stopping an unowned Sandbox must be advisory.");
        Assert.AreEqual(0, _cli.StartCount, "An unmanaged Sandbox must never be replaced.");
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped, "An unmanaged Sandbox must never be stopped.");
    }

    [TestMethod]
    public async Task EnsureInstance_ExternallyStopped_RecoversWithANewEpoch()
    {
        _cli.StartIds.Enqueue("sandbox-a");
        var first = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        // The user closed the Sandbox or ran `wsb stop`.
        _cli.SetRunning();

        var reconciled = await _lifecycle.ReconcileAsync(TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(TargetLifecycleState.Terminated, reconciled.State);

        _cli.StartIds.Enqueue("sandbox-b");
        var second = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-b", second.InstanceId);
        Assert.IsFalse(second.Reused);

        // Handles captured against the old generation must not resolve against the new guest.
        Assert.AreNotEqual(first.Epoch, second.Epoch);
    }

    [TestMethod]
    public async Task EnsureInstance_SameIdReusedAfterReboot_StillProducesANewEpoch()
    {
        _cli.StartIds.Enqueue("sandbox-a");
        var first = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        _cli.SetRunning();
        _lifecycle.InvalidateManagedInstance();

        // Windows could hand back an identical ID; the boot nonce is what guarantees a fresh epoch.
        _cli.StartIds.Enqueue("sandbox-a");
        var second = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreNotEqual(first.Epoch, second.Epoch);
    }

    [TestMethod]
    public async Task EnsureInstance_OwnRecordedInstance_IsNotMisreportedAsUnmanaged()
    {
        _cli.StartIds.Enqueue("sandbox-a");
        await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        // A second, foreign instance appearing alongside ours is what must be refused — not our own.
        _cli.SetRunning("sandbox-a");
        var reused = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(reused.Reused);
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped);
    }

    [TestMethod]
    public async Task InvalidateManagedInstance_ClearsOwnership()
    {
        _cli.StartIds.Enqueue("sandbox-a");
        await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        _lifecycle.InvalidateManagedInstance();

        Assert.IsNull(_stateStore.Read(ExecutionTargetRef.WindowsSandboxDefault));
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

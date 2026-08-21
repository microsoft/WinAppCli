// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="TargetMutationLock"/>, the cross-process gate that serializes guest
/// mutation. Because there is no persistent host coordinator, this lock is the only thing
/// preventing two winapp processes from mutating the same guest environment concurrently.
/// </summary>
[TestClass]
public class TargetMutationLockTests
{
    private ExecutionTargetRef _target = null!;
    private TargetMutationLock _lock = null!;

    [TestInitialize]
    public void Setup()
    {
        // A unique target id per test keeps the named mutex isolated from other tests and from any
        // real winapp process running on the same machine.
        _target = new ExecutionTargetRef("windows-sandbox", $"windows-sandbox:test-{Guid.NewGuid():N}");
        _lock = new TargetMutationLock();
    }

    [TestMethod]
    public void TryAcquire_WhenFree_Succeeds()
    {
        using var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(lease);
        Assert.IsFalse(lease.WasAbandoned);
    }

    [TestMethod]
    public void TryAcquire_WhileHeldByAnotherThread_TimesOut()
    {
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            using var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        holder.Start();

        try
        {
            Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(5)), "Holder thread failed to acquire.");

            var contended = _lock.TryAcquire(_target, TimeSpan.FromMilliseconds(200));

            Assert.IsNull(contended, "A held mutation lock must not be handed to a second caller.");
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    public void TryAcquire_AfterRelease_SucceedsAgain()
    {
        using (var first = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5)))
        {
            Assert.IsNotNull(first);
        }

        using var second = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(second);
        Assert.IsFalse(second.WasAbandoned, "A cleanly released lock is not an abandoned one.");
    }

    [TestMethod]
    public void TryAcquire_AfterOwnerDiesWithoutReleasing_ReportsAbandonedAsRecoverySignal()
    {
        // Abandon the mutex the way a crashed host process would: acquire it on a thread that
        // exits without releasing. The next owner must be told so it can reconcile a possibly
        // half-mutated guest rather than assuming the environment is clean.
        var abandoner = new Thread(() =>
        {
            var mutex = new Mutex(initiallyOwned: false, TargetMutationLock.GetMutexName(_target));
            mutex.WaitOne(TimeSpan.FromSeconds(5));
        });
        abandoner.Start();
        abandoner.Join(TimeSpan.FromSeconds(10));

        using var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(lease);
        Assert.IsTrue(lease.WasAbandoned, "An abandoned mutex must surface as a recovery signal.");
    }

    [TestMethod]
    public void TryAcquire_Cancelled_ThrowsWithoutLeakingTheLock()
    {
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            using var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        holder.Start();

        try
        {
            Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(5)), "Holder thread failed to acquire.");

            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

            Assert.ThrowsExactly<OperationCanceledException>(
                () => _lock.TryAcquire(_target, TimeSpan.FromSeconds(30), cancellation.Token));
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }

        // Cancellation must not have taken ownership; the lock is free once the holder released it.
        using var afterCancel = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(afterCancel);
    }

    [TestMethod]
    public void MutexName_IsPerTargetAndSessionScoped()
    {
        var sandbox = TargetMutationLock.GetMutexName(ExecutionTargetRef.WindowsSandboxDefault);
        var other = TargetMutationLock.GetMutexName(new ExecutionTargetRef("hyperv", "hyperv:winui-test"));

        // Session-scoped so it matches the per-user state root instead of colliding across users.
        StringAssert.StartsWith(sandbox, "Local\\", StringComparison.Ordinal);
        Assert.AreEqual("Local\\winapp-target-windows-sandbox-default-mutation", sandbox);

        // Per-target so future targets never serialize against each other.
        Assert.AreNotEqual(sandbox, other);
    }

    [TestMethod]
    public void DifferentTargets_DoNotBlockEachOther()
    {
        var otherTarget = new ExecutionTargetRef("hyperv", $"hyperv:test-{Guid.NewGuid():N}");

        using var first = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        using var second = _lock.TryAcquire(otherTarget, TimeSpan.FromMilliseconds(500));

        Assert.IsNotNull(first);
        Assert.IsNotNull(second, "Independent targets must be able to mutate concurrently.");
    }
}

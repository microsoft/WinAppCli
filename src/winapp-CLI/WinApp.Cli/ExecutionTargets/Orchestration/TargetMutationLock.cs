// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Held ownership of a target's mutation lock. Disposing releases it.
/// </summary>
internal sealed class TargetMutationLease : IDisposable
{
    private Mutex? _mutex;

    internal TargetMutationLease(Mutex mutex, bool wasAbandoned)
    {
        _mutex = mutex;
        WasAbandoned = wasAbandoned;
    }

    /// <summary>
    /// True when the previous owner died without releasing the lock.
    /// </summary>
    /// <remarks>
    /// This is a recovery signal, not an error: the guest environment may have been left
    /// half-mutated. The caller must verify epoch and dirty state and reconcile before mutating
    /// further (spec §"Host coordination and state").
    /// </remarks>
    public bool WasAbandoned { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner, which can only happen if the lease was disposed on a different thread
            // than it was acquired on. Disposing the handle below is still correct and complete.
        }
        finally
        {
            mutex.Dispose();
        }
    }
}

/// <summary>Serializes guest-mutating operations for one target across host processes.</summary>
internal interface ITargetMutationLock
{
    /// <summary>
    /// Acquires the lock, or returns <see langword="null"/> if <paramref name="timeout"/> elapses.
    /// </summary>
    TargetMutationLease? TryAcquire(
        ExecutionTargetRef target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Named-mutex implementation of <see cref="ITargetMutationLock"/> (spec §"Host coordination and
/// state": "one per-target named mutation mutex").
/// </summary>
/// <remarks>
/// There is no persistent host coordinator process, so mutual exclusion has to come from the OS.
/// The mutex covers Sandbox creation and repair, guest winapp replacement, shared runtime
/// installation, deployment synchronization, and package registration. It deliberately does
/// <em>not</em> cover host build, running applications, or read-only UI Automation, so a long build
/// or a running app never blocks another workflow.
/// <para>
/// The name is per-target, so future targets serialize independently. The <c>Local\</c> namespace
/// scopes it to the logon session, matching the per-user state root and avoiding the cross-user
/// collisions a <c>Global\</c> name would introduce.
/// </para>
/// <para>
/// This lock is unrelated to Cooperative UI Turns. It protects guest environment and deployment
/// mutations, not the interactive desktop.
/// </para>
/// </remarks>
internal sealed class TargetMutationLock : ITargetMutationLock
{
    /// <summary>Builds the kernel object name for <paramref name="target"/>.</summary>
    internal static string GetMutexName(ExecutionTargetRef target) =>
        $"Local\\winapp-target-{target.Slug}-mutation";

    /// <inheritdoc/>
    public TargetMutationLease? TryAcquire(
        ExecutionTargetRef target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var mutex = new Mutex(initiallyOwned: false, GetMutexName(target));
        try
        {
            return WaitAndWrap(mutex, timeout, cancellationToken);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static TargetMutationLease? WaitAndWrap(
        Mutex mutex,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var wasAbandoned = false;
        bool acquired;
        try
        {
            acquired = Wait(mutex, timeout, cancellationToken);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died holding the lock. We now own it; the caller reconciles.
            acquired = true;
            wasAbandoned = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new TargetMutationLease(mutex, wasAbandoned);
    }

    /// <summary>
    /// Waits on the mutex while remaining responsive to cancellation, which a bare
    /// <see cref="WaitHandle.WaitOne(TimeSpan)"/> is not.
    /// </summary>
    private static bool Wait(Mutex mutex, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return mutex.WaitOne(timeout);
        }

        using var cancellationEvent = new ManualResetEventSlim(false);
        using var registration = cancellationToken.Register(cancellationEvent.Set);

        var index = WaitHandle.WaitAny([mutex, cancellationEvent.WaitHandle], timeout);
        if (index == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return index == 0;
    }
}

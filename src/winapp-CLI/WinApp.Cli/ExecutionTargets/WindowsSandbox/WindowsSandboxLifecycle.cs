// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Outcome of reconciling persisted state against what is actually running.</summary>
/// <param name="State">How the managed instance is currently classified.</param>
/// <param name="InstanceId">The managed instance, when one exists.</param>
/// <param name="Epoch">Generation identity of that instance.</param>
/// <param name="Revision">State revision the caller must pass when committing.</param>
internal sealed record SandboxReconciliation(
    TargetLifecycleState State,
    string? InstanceId,
    ExecutionTargetEpoch Epoch,
    long Revision);

/// <summary>Result of ensuring a managed Sandbox exists.</summary>
/// <param name="InstanceId">The managed instance.</param>
/// <param name="Epoch">Its generation identity.</param>
/// <param name="Reused">True when an existing healthy instance was reused.</param>
internal sealed record SandboxInstanceLease(string InstanceId, ExecutionTargetEpoch Epoch, bool Reused);

/// <summary>
/// Owns the Windows Sandbox singleton: which instance winapp created, whether it still exists, and
/// when it is safe to create another (spec §"Sandbox lifecycle").
/// </summary>
/// <remarks>
/// Windows permits only one Sandbox at a time, so this type is deliberately conservative. It adopts
/// nothing it did not create and stops nothing it does not own: an instance winapp cannot prove it
/// created may hold someone else's work, and silently reclaiming it would destroy that work.
/// <para>
/// Reuse currently rests on the instance still being listed. Waiting for a <em>tearing-down</em>
/// singleton is not implemented here because that state is not observable from <c>wsb list</c>
/// alone — an instance is either listed or not. It becomes both observable and reachable once the
/// guest agent heartbeat lands, at which point "recorded and listed but unhealthy" is a real
/// classification that has to recreate the instance.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxLifecycle(
    IWindowsSandboxCli cli,
    ITargetStateStore stateStore)
{
    private readonly ExecutionTargetRef _target = ExecutionTargetRef.WindowsSandboxDefault;

    /// <summary>
    /// Classifies the managed instance by comparing persisted state against <c>wsb list</c>.
    /// </summary>
    /// <remarks>
    /// An instance that vanished — because the user closed it or ran <c>wsb stop</c> — is reported
    /// as <see cref="TargetLifecycleState.Terminated"/> so its process, deployment, and handle state
    /// is invalidated rather than resolved against whatever is created next.
    /// </remarks>
    public async Task<SandboxReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        var state = stateStore.Read(_target);
        var running = await cli.ListAsync(cancellationToken).ConfigureAwait(false);
        var revision = state?.Revision ?? 0;

        if (state?.InstanceId is not { } managedId || string.IsNullOrWhiteSpace(state.BootNonce))
        {
            return new SandboxReconciliation(TargetLifecycleState.Terminated, null, ExecutionTargetEpoch.None, revision);
        }

        if (!running.Contains(managedId, StringComparer.OrdinalIgnoreCase))
        {
            // Our instance is gone. Report Terminated so the caller invalidates everything scoped to
            // the old epoch before anything new is created.
            return new SandboxReconciliation(TargetLifecycleState.Terminated, null, ExecutionTargetEpoch.None, revision);
        }

        return new SandboxReconciliation(
            TargetLifecycleState.Running,
            managedId,
            ExecutionTargetEpoch.Create(managedId, state.BootNonce),
            revision);
    }

    /// <summary>
    /// Returns a managed Sandbox, reusing a healthy one or creating a new one.
    /// </summary>
    /// <remarks>
    /// Callers must already hold the target mutation lock: creating a Sandbox mutates the singleton.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// An instance winapp cannot prove it owns is running.
    /// </exception>
    public async Task<SandboxInstanceLease> EnsureInstanceAsync(CancellationToken cancellationToken)
    {
        var state = stateStore.Read(_target);
        var revision = state?.Revision ?? 0;
        var recordedId = state?.InstanceId;
        var running = await cli.ListAsync(cancellationToken).ConfigureAwait(false);

        if (recordedId is { } managedId
            && state?.BootNonce is { } bootNonce
            && running.Contains(managedId, StringComparer.OrdinalIgnoreCase))
        {
            return new SandboxInstanceLease(
                managedId,
                ExecutionTargetEpoch.Create(managedId, bootNonce),
                Reused: true);
        }

        RejectUnmanagedInstance(running, recordedId);

        var instanceId = await cli.StartAsync(configuration: null, cancellationToken).ConfigureAwait(false);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        try
        {
            stateStore.Commit(
                _target,
                new TargetState
                {
                    SchemaVersion = 0,
                    Revision = 0,
                    TargetKind = _target.Kind,
                    TargetId = _target.Id,
                    InstanceId = instanceId,
                    BootNonce = nonce,
                },
                revision);
        }
        catch
        {
            // The Sandbox exists but ownership was never recorded. Left alone it becomes an
            // instance winapp cannot prove it created, which every later command would refuse --
            // permanently wedging the target through no fault of the user. Undo the one thing this
            // call created, then report the original failure.
            await StopUnownedInstanceAsync(instanceId).ConfigureAwait(false);
            throw;
        }

        return new SandboxInstanceLease(instanceId, ExecutionTargetEpoch.Create(instanceId, nonce), Reused: false);
    }

    /// <summary>
    /// Best-effort stop of an instance this call created but could not record.
    /// </summary>
    /// <remarks>
    /// Uses its own bounded timeout rather than the caller's token: the caller's token may already
    /// be cancelled, and compensation still needs to run. Failures are swallowed so the original,
    /// more informative error is what surfaces.
    /// </remarks>
    private async Task StopUnownedInstanceAsync(string instanceId)
    {
        using var compensation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await cli.StopAsync(instanceId, compensation.Token).ConfigureAwait(false);
        }
        catch (ExecutionTargetException)
        {
            // Nothing more can be done; the original failure is the one worth reporting.
        }
        catch (OperationCanceledException)
        {
            // Compensation timed out.
        }
    }

    /// <summary>
    /// Fails when a Sandbox winapp did not create is running.
    /// </summary>
    /// <remarks>
    /// The instance winapp itself recorded is excluded, so an instance of ours that is on its way
    /// out is never misreported as belonging to someone else.
    /// <para>
    /// The stop command is offered as advisory guidance only. It is never run automatically and
    /// never presented as a safe default, because only the user knows whether that Sandbox holds
    /// work worth keeping.
    /// </para>
    /// </remarks>
    private static void RejectUnmanagedInstance(IReadOnlyList<string> running, string? recordedId)
    {
        var unmanagedId = running.FirstOrDefault(
            id => !string.Equals(id, recordedId, StringComparison.OrdinalIgnoreCase));

        if (unmanagedId is null)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.UnmanagedInstance,
            "Another Windows Sandbox instance is already running.",
            userAction: "Close the existing Sandbox if it is safe to do so, then retry.",
            context: new Dictionary<string, string> { ["sandboxId"] = unmanagedId },
            nextCommand: new ExecutionTargetNextCommand
            {
                Command = $"wsb stop --id {unmanagedId}",

                // Advisory: stopping a Sandbox winapp does not own can destroy the user's work.
                Advisory = true,
            },
            example: "winapp run . --sandbox");
    }

    /// <summary>
    /// Forgets the managed instance after it has been observed gone.
    /// </summary>
    /// <remarks>
    /// Clearing state is what makes the next command treat handles and deployments from the old
    /// epoch as stale rather than resolving them against a recreated guest.
    /// </remarks>
    public void InvalidateManagedInstance() => stateStore.Clear(_target);
}

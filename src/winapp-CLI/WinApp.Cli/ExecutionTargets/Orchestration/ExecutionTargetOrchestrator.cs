// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>A prepared execution target: a live channel, its epoch, and how it was obtained.</summary>
/// <param name="Channel">Ready command channel to the guest agent.</param>
/// <param name="Epoch">Generation identity every request and result is fenced against.</param>
/// <param name="Capabilities">What the guest reported it can do.</param>
/// <param name="Reused">True when an existing instance was reused, driving the progress line.</param>
internal sealed record PreparedTarget(
    GuestCommandChannel Channel,
    ExecutionTargetEpoch Epoch,
    ExecutionTargetCapabilities Capabilities,
    bool Reused,
    TargetConnectionLease? ConnectionLease = null) : IAsyncDisposable
{
    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Channel.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ConnectionLease?.Dispose();
        }
    }
}

/// <summary>What a command needs from the target before it will run.</summary>
/// <param name="RequireInteractiveDesktop">
/// True for real input or screen capture, which need a connected interactive client. False for
/// read-only work such as UI Automation inspection.
/// </param>
/// <param name="RequiresMutation">
/// True when the command will change guest state, which is what the mutation lock covers.
/// </param>
internal sealed record PrepareTargetOptions(bool RequireInteractiveDesktop, bool RequiresMutation)
{
    /// <summary>Deployment, registration, and runtime installation.</summary>
    public static PrepareTargetOptions Mutating { get; } = new(true, true);

    /// <summary>Foreground-sensitive commands that change nothing in the guest.</summary>
    public static PrepareTargetOptions Interactive { get; } = new(true, false);

    /// <summary>Read-only inspection, which needs neither the desktop nor the lock.</summary>
    public static PrepareTargetOptions ReadOnly { get; } = new(false, false);
}

/// <summary>
/// The single entry point every <c>--sandbox</c> command goes through
/// (spec §"Ensure and reuse", §"Shared orchestration").
/// </summary>
/// <remarks>
/// Ordering here is the contract. Support is probed before anything is built or mutated, so a
/// missing prerequisite fails in seconds rather than after a long build and never falls back
/// silently to local execution. The mutation lock is taken only when the command will actually
/// change guest state, so a read-only inspection never blocks behind a deployment — and, equally,
/// never blocks one.
/// <para>
/// Agent version negotiation happens before the first real operation but after the channel exists,
/// because the decision needs what the guest actually reports rather than what the host assumes.
/// </para>
/// </remarks>
internal sealed class ExecutionTargetOrchestrator(
    IExecutionTargetBackend backend,
    ITargetMutationLock mutationLock,
    ITargetConnectionLock connectionLock)
{
    /// <summary>How long to wait for another winapp process to finish mutating this target.</summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cheaply checks that this host can use the target at all, before any build or mutation.
    /// </summary>
    /// <exception cref="ExecutionTargetException">The target is not usable on this host.</exception>
    public async Task EnsureSupportedAsync(CancellationToken cancellationToken)
    {
        var support = await backend.ProbeSupportAsync(cancellationToken).ConfigureAwait(false);

        if (!support.IsSupported)
        {
            throw new ExecutionTargetException(support.Error ?? new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.Unsupported,
                Message = "This host cannot run commands in an execution target.",
            });
        }
    }

    /// <summary>
    /// Ensures a running target with a compatible agent and returns a ready command channel.
    /// </summary>
    /// <remarks>
    /// The caller owns the returned <see cref="PreparedTarget"/> and must dispose it; the mutation
    /// lock, by contrast, is released as soon as the target is prepared. Holding it for the life of
    /// a running application would mean one long-running app blocked every other workflow, which is
    /// exactly what the spec excludes from the lock's scope.
    /// </remarks>
    public async Task<PreparedTarget> PrepareAsync(
        PrepareTargetOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        await EnsureSupportedAsync(cancellationToken).ConfigureAwait(false);

        var connectionLease = AcquireConnection(cancellationToken);
        GuestCommandChannel? channel = null;

        try
        {
            var connection = await backend.EnsureConnectedAsync(
                new EnsureTargetOptions(options.RequireInteractiveDesktop),
                cancellationToken).ConfigureAwait(false);
            channel = new GuestCommandChannel(connection.Transport, connection.Epoch);
            channel.Start();

            using var mutationLease = options.RequiresMutation ? AcquireLock(cancellationToken) : null;

            if (mutationLease?.WasAbandoned == true)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "The previous winapp process did not release the {0} lock cleanly; reconciling.",
                    backend.Target.Id);
            }

            var capabilities = await channel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

            EnsureCapable(options, capabilities);

            var prepared = new PreparedTarget(
                channel,
                connection.Epoch,
                capabilities,
                connection.Reused,
                connectionLease);

            channel = null;
            connectionLease = null;
            return prepared;
        }
        catch
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            connectionLease?.Dispose();
        }
    }

    /// <summary>Progress line for a prepared target, matching the spec's exact wording.</summary>
    /// <remarks>
    /// No Sandbox ID or lifecycle guidance is printed during successful normal use: it is noise the
    /// user cannot act on, and printing it would train them to expect it in failures too.
    /// </remarks>
    public static string DescribeProgress(bool reused) =>
        reused ? "Reusing Windows Sandbox..." : "Preparing Windows Sandbox...";

    /// <summary>Provider diagnostics for a failure envelope.</summary>
    public IReadOnlyDictionary<string, string> DescribeForDiagnostics() => backend.DescribeForDiagnostics();

    private TargetMutationLease AcquireLock(CancellationToken cancellationToken)
    {
        var lease = mutationLock.TryAcquire(backend.Target, LockTimeout, cancellationToken);

        return lease ?? throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetAmbiguous,
            "Another winapp command is still changing this Windows Sandbox.",
            userAction: "Wait for the other command to finish, then retry.",
            context: new Dictionary<string, string> { ["targetId"] = backend.Target.Id });
    }

    private TargetConnectionLease AcquireConnection(CancellationToken cancellationToken)
    {
        return connectionLock.TryAcquire(backend.Target, LockTimeout, cancellationToken)
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "Another winapp command is still using the Windows Sandbox agent.",
                userAction: "Wait for the other command to finish, then retry.",
                context: new Dictionary<string, string> { ["targetId"] = backend.Target.Id });
    }

    /// <summary>
    /// Refuses a command whose required capability the guest does not have.
    /// </summary>
    /// <remarks>
    /// Checked against what the guest reports rather than inferred from the provider name, which is
    /// what lets a future backend reuse this unchanged.
    /// <para>
    /// The capability consulted is <see cref="ExecutionTargetCapabilities.SupportsRealInput"/>, not
    /// <see cref="ExecutionTargetCapabilities.SupportsInteractiveDesktop"/>. Those differ in exactly
    /// the case that matters: a closed Sandbox client leaves the guest session and UI Automation
    /// working — so an interactive desktop is still reported — while real input and Windows Graphics
    /// Capture stop. Gating on the wrong one would admit foreground commands that then report input
    /// they never delivered.
    /// </para>
    /// <para>
    /// This is still capability, not readiness. A command that delivers input re-verifies
    /// immediately beforehand, because the user can disconnect between this check and the keystroke.
    /// </para>
    /// </remarks>
    private static void EnsureCapable(PrepareTargetOptions options, ExecutionTargetCapabilities capabilities)
    {
        if (!options.RequireInteractiveDesktop)
        {
            return;
        }

        if (capabilities.SupportsRealInput)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            capabilities.SupportsInteractiveDesktop
                ? ExecutionTargetErrorCodes.InputNotReady
                : ExecutionTargetErrorCodes.NoInteractiveSession,
            capabilities.SupportsInteractiveDesktop
                ? "The Windows Sandbox window is disconnected, so real input and screen capture are unavailable."
                : "The execution target has no interactive desktop, so this command cannot run there.",
            userAction: "Reconnect the Sandbox window, then retry.",
            nextCommand: new ExecutionTargetNextCommand
            {
                Command = "wsb connect",

                // Reconnecting changes what is on screen, so it needs a user decision.
                Advisory = true,
            });
    }
}

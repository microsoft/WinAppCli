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
/// <remarks>
/// Deliberately owns no lock. The connection lock covers establishing a channel, not using one, so
/// a prepared target — which lives for as long as the command that holds it, including a foreground
/// application — never keeps another winapp process from connecting.
/// </remarks>
internal sealed record PreparedTarget(
    GuestCommandChannel Channel,
    ExecutionTargetEpoch Epoch,
    ExecutionTargetCapabilities Capabilities,
    bool Reused) : IAsyncDisposable
{
    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Channel.DisposeAsync();
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
    ITargetConnectionLock connectionLock,
    ITargetProgress? progress = null)
{
    private readonly ITargetProgress _progress = progress ?? NullTargetProgress.Instance;

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
    /// The caller owns the returned <see cref="PreparedTarget"/> and must dispose it. Neither lock
    /// outlives this method. The mutation lock is released as soon as the target is prepared, and
    /// the connection lock is released the moment the channel exists — holding either for the life
    /// of a running application would mean one long-running app blocked every other workflow, which
    /// is exactly what the spec excludes from their scope.
    /// <para>
    /// The connection lock's remaining job is to make establishment safe: it spans the reconnect
    /// attempt and the bootstrap that follows a failed one, so two hosts starting at once cannot
    /// both create an instance, rewrite connection material, or replace the agent. Whichever gets in
    /// first bootstraps; the second finds a healthy agent and reconnects to it.
    /// </para>
    /// </remarks>
    public async Task<PreparedTarget> PrepareAsync(
        PrepareTargetOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Reported before the probe rather than after it, because everything from here on can block
        // for seconds and the user is looking at a terminal that has just gone quiet.
        _progress.Report("Checking Windows Sandbox availability...");

        await EnsureSupportedAsync(cancellationToken).ConfigureAwait(false);

        GuestCommandChannel? channel = null;

        try
        {
            TargetConnection connection;

            using (AcquireConnection(cancellationToken))
            {
                connection = await backend.EnsureConnectedAsync(
                    new EnsureTargetOptions(options.RequireInteractiveDesktop),
                    cancellationToken).ConfigureAwait(false);

                channel = new GuestCommandChannel(connection.Transport, connection.Epoch);
                channel.Start();
            }

            // Negotiated before any lock is taken, not after. A guest that is refusing this channel
            // says so here, and waiting up to the lock timeout first would turn an immediate,
            // actionable "the agent is busy" into a stale channel and a confusing transport error.
            var capabilities = await channel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

            using var mutationLease = options.RequiresMutation ? AcquireLock(cancellationToken) : null;

            if (mutationLease?.WasAbandoned == true)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "The previous winapp process did not release the {0} lock cleanly; reconciling.",
                    backend.Target.Id);
            }

            EnsureCapable(options, capabilities);

            var prepared = new PreparedTarget(
                channel,
                connection.Epoch,
                capabilities,
                connection.Reused);

            channel = null;
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
    }

    /// <summary>Progress line for a prepared target, matching the spec's exact wording.</summary>
    /// <remarks>
    /// No Sandbox ID or lifecycle guidance is printed during successful normal use: it is noise the
    /// user cannot act on, and printing it would train them to expect it in failures too.
    /// <para>
    /// Retained for callers that render their own status. The orchestrator itself does not print
    /// this, because the backend already reports the specific phase it is in — announcing
    /// "Preparing Windows Sandbox..." after preparation has finished describes the past as if it
    /// were the present.
    /// </para>
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
                "Another winapp command is still starting or repairing this Windows Sandbox.",
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

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Cooperative desktop turn coordination for concurrent <c>winapp ui</c> processes (issue #764).
/// </summary>
/// <remarks>
/// <para>
/// Lock ordering is mandatory and enforced by the shape of this class (spec §9): <c>state.lock</c> is
/// only ever held inside a <c>using</c> that contains no awaits on UI work, a participant lease is
/// always opened before its state entry is published and closed after the entry is removed, and
/// <c>active.lock</c> is never acquired while <c>state.lock</c> is held.
/// </para>
/// <para>
/// The forward barrier wraps command execution, but <c>active.lock</c> deliberately does not: a
/// command takes it only around the moment it touches the shared desktop, so output formatting, PNG
/// encoding, file publication and logging never block another workflow.
/// </para>
/// </remarks>
internal sealed class InteractiveDesktopLock : IInteractiveDesktopLock
{
    /// <summary>Exit code for a command cancelled before it ever ran (128 + SIGINT).</summary>
    internal const int CancelledExitCode = 130;

    private const int PollMinMs = 50;
    private const int PollMaxMs = 75;
    private const int ActiveLockRetryMinMs = 10;
    private const int ActiveLockRetryMaxMs = 25;

    // Explicit fields rather than primary-constructor parameters: the nested CoordinatedExecution needs
    // access to them, and captured primary-constructor parameters are not visible to nested types.
    private readonly IInteractiveDesktopStateStore _store;
    private readonly IInteractiveDesktopPaths _paths;
    private readonly IParticipantRegistry _participants;
    private readonly IUiOwnerResolver _ownerResolver;
    private readonly IProcessInspector _processInspector;
    private readonly IPollDelay _pollDelay;
    private readonly IAnsiConsole _console;
    private readonly ILogger<InteractiveDesktopLock> _logger;
    private readonly IMonotonicClock _clock;
    private readonly InteractiveDesktopScheduler _scheduler;

    public InteractiveDesktopLock(
        IInteractiveDesktopStateStore store,
        IInteractiveDesktopPaths paths,
        IParticipantRegistry participants,
        IUiOwnerResolver ownerResolver,
        IProcessInspector processInspector,
        IMonotonicClock clock,
        IPollDelay pollDelay,
        IAnsiConsole console,
        ILogger<InteractiveDesktopLock> logger)
    {
        _store = store;
        _paths = paths;
        _participants = participants;
        _ownerResolver = ownerResolver;
        _processInspector = processInspector;
        _pollDelay = pollDelay;
        _console = console;
        _logger = logger;
        _clock = clock;
        _scheduler = new InteractiveDesktopScheduler(clock);
    }

    public async Task<int> RunCoordinatedAsync(
        UiTurnMode mode,
        string operation,
        ParseResult parseResult,
        Func<IUiTurn, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        var outputMode = UiCoordinationOutputMode.FromParseResult(parseResult);
        var owner = _ownerResolver.Resolve();
        var participant = new UiParticipantIdentity(
            _processInspector.CurrentProcessId,
            _processInspector.CurrentProcessStartTicksUtc,
            operation);

        var execution = new CoordinatedExecution(this, owner, participant, mode, outputMode, parseResult);
        using (execution)
        {
            return await execution.RunAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private LivenessProbe CreateProbe() => new LivenessProbe(_participants);

    /// <summary>
    /// Waits for <c>active.lock</c>. Never steals it from a live process — a hung owner is recovered by
    /// cancelling or terminating it, not by another process forcing its way onto the desktop (spec §7.3).
    /// </summary>
    private async Task<FileStream> AcquireActiveLockAsync(string activeLockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    activeLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException ex) when (CoordinationLockIo.IsContention(ex))
            {
                // Held by a process inside its desktop-sensitive section.
            }
            catch (IOException ex)
            {
                // Not contention: retrying would wait forever on a failure that will never clear.
                throw CoordinationLockIo.CannotOpen(activeLockPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    $"The UI desktop lock could not be opened: {ex.Message}",
                    "Check that the current user can write to the coordination directory.");
            }

            await _pollDelay
                .DelayAsync(Random.Shared.Next(ActiveLockRetryMinMs, ActiveLockRetryMaxMs + 1), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Lease-backed participant liveness, the only basis for pruning.</summary>
    private sealed class LivenessProbe(IParticipantRegistry participants)
        : ICoordinationLivenessProbe
    {
        public bool IsParticipantLive(int processId, long startTicksUtc)
            => participants.IsParticipantLive(processId, startTicksUtc);
    }

    /// <summary>
    /// One command's participation: registration, queue waiting, desktop sections, escalation and
    /// teardown. Held as a separate object so <see cref="InteractiveDesktopLock"/> itself stays a
    /// stateless singleton.
    /// </summary>
    private sealed class CoordinatedExecution(
        InteractiveDesktopLock coordinator,
        UiOwnerIdentity owner,
        UiParticipantIdentity participant,
        UiTurnMode mode,
        UiCoordinationOutputMode outputMode,
        ParseResult parseResult) : IUiTurn, IDisposable
    {
        private readonly LivenessProbe _probe = coordinator.CreateProbe();
        private readonly Stopwatch _waitWatch = new();

        /// <summary>
        /// Serializes desktop sections opened by this one command.
        /// </summary>
        /// <remarks>
        /// <c>active.lock</c> is <c>FileShare.None</c>, so a second handle blocks even from the same
        /// process. Without this gate, two concurrent tasks in one command would race on the file lock;
        /// with an earlier refcount design one of them would have skipped the lock entirely and run its
        /// desktop work unprotected. The gate makes unrelated concurrent enters queue up instead.
        /// <para>
        /// Desktop sections are deliberately NOT reentrant. Every call site is sequential: the screenshot
        /// pass enters once per restore/foreground/live-screen moment and closes each scope before the
        /// next, and recording enters once before capture plus once per rare blank-frame retry. Code that
        /// genuinely needs the desktop inside an open section must receive the existing
        /// <see cref="IDesktopSection"/> scope rather than opening a nested one, which would self-deadlock
        /// on the file lock.
        /// </para>
        /// </remarks>
        private readonly SemaphoreSlim _sectionGate = new(1, 1);

        private IParticipantLease? _lease;
        private FileStream? _activeLock;        private bool _detached;
        private bool _recoveredFromCorruption;
        private long? _ticket;
        private UiTurnAction _turnAction = UiTurnAction.New;
        private int _observedQueueDepth;

        /// <summary>
        /// Monotonic tick at which the turn this command runs under was claimed, copied from the state
        /// every time this command observes the turn it belongs to. Reported as the turn-age bucket, so
        /// it measures how long the whole workflow has held the desktop rather than how long this one
        /// command waited (spec §16). Null for detached observations, which hold no turn.
        /// </summary>
        private long? _turnStartedTick64;

        public UiTurnMode Mode { get; private set; } = mode;

        public long WaitedMs { get; private set; }

        public async Task<int> RunAsync(
            Func<IUiTurn, CancellationToken, Task<int>> body,
            CancellationToken cancellationToken)
        {
            var bodyCompletedNormally = false;
            var outcome = UiCoordinationOutcome.Completed;

            try
            {
                Register(cancellationToken);

                if (!_detached)
                {
                    await WaitUntilRunnableAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var exitCode = await body(this, cancellationToken).ConfigureAwait(false);

                    // "Completed normally" means the body RETURNED rather than threw — deliberately not
                    // "the token is unset". `ui record` observes Ctrl+C on purpose, finalizes the MP4 and
                    // returns success; that is a completed command and must renew the owner's grace.
                    // Commands that must not renew let the cancellation propagate instead, which is why
                    // handler catch-alls are filtered with UiCoordinatedAction.IsCoordinationFault.
                    bodyCompletedNormally = true;
                    return exitCode;
                }
                finally
                {
                    await ReleaseAllSectionsAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!bodyCompletedNormally)
            {
                // The body threw rather than returned, so there is no result to preserve (spec §11.1 for
                // a queued command; §11.2 for one cancelled after acquisition, whose command semantics are
                // "it produced nothing"). Either way the command must not renew the owner's idle grace,
                // which the `finally` below guarantees by passing bodyCompletedNormally: false.
                //
                // A command that DOES have something to preserve — an active `ui record` finalizing its
                // MP4 on Ctrl+C — returns instead of throwing, never reaches here, and still renews.
                outcome = UiCoordinationOutcome.Cancelled;
                EmitCancellation(cancelledWhileQueued: _waitWatch.IsRunning);
                return CancelledExitCode;
            }
            catch (UiCoordinationException)
            {
                outcome = UiCoordinationOutcome.CoordinationFailure;
                throw;
            }
            finally
            {
                Complete(bodyCompletedNormally);
                PublishTelemetry(bodyCompletedNormally, outcome);
            }
        }

        private void Register(CancellationToken cancellationToken)
        {
            using var stateLock = coordinator._store.AcquireStateLock(cancellationToken);
            var read = coordinator._store.Read();
            _recoveredFromCorruption = read.RecoveredFromCorruption;

            if (read.UnknownNewerVersion)
            {
                RegisterAgainstUnknownVersion();
                return;
            }

            var state = read.State!;

            if (Mode == UiTurnMode.Observe)
            {
                RegisterObserve(state);
                return;
            }

            RegisterParticipating(state);
        }

        /// <summary>
        /// A newer binary owns the state file. Observations continue detached without touching it;
        /// anything that would claim or mutate a turn fails closed rather than driving the desktop
        /// outside coordination (spec §12.4).
        /// </summary>
        private void RegisterAgainstUnknownVersion()
        {
            if (Mode != UiTurnMode.Observe)
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    "UI turn coordination state was written by a newer version of winapp, so this build cannot coordinate safely.",
                    "Update winapp so every process on this desktop uses a compatible version, then retry.");
            }

            coordinator._logger.LogDebug(
                "UI coordination state has a newer schema version; running {Operation} detached.", participant.Operation);
            _detached = true;
            _turnAction = UiTurnAction.Detached;
        }

        private void RegisterObserve(InteractiveDesktopState state)
        {
            var changed = coordinator._scheduler.Normalize(state, _probe);

            if (!InteractiveDesktopScheduler.IsCurrentOwner(state, owner))
            {
                // Spec §6.2: a non-owner observation never claims a free turn, so it runs with no lease
                // and no state entry and cannot block anyone.
                if (changed || _recoveredFromCorruption)
                {
                    coordinator._store.Publish(state);
                }

                _detached = true;
                _turnAction = UiTurnAction.Detached;
                return;
            }

            // The lease is opened before the entry is published so no published command ever lacks
            // liveness proof (spec §9 rule 5).
            _lease = coordinator._participants.OpenLease(participant.ProcessId, participant.StartTicksUtc);
            var admission = coordinator._scheduler.BeginObserve(state, _probe, owner, participant);

            if (admission.Admission == UiAdmission.Detached)
            {
                // BeginObserve re-normalizes, so ownership can lapse between the check above and here —
                // an expiring grace released by normalization. Nothing was added to
                // the state, so the lease must go too: keeping it open would publish liveness for a
                // participant with no entry, and Complete would later adjust a foreign owner's turn.
                _lease.Dispose();
                _lease = null;
                _detached = true;
                _turnAction = UiTurnAction.Detached;

                if (changed || _recoveredFromCorruption)
                {
                    coordinator._store.Publish(state);
                }

                return;
            }

            _turnAction = admission.TurnAction;
            _turnStartedTick64 = TurnStartTick(state);
            coordinator._store.Publish(state);
        }

        private void RegisterParticipating(InteractiveDesktopState state)
        {
            _lease = coordinator._participants.OpenLease(participant.ProcessId, participant.StartTicksUtc);

            UiAdmissionResult admission;
            try
            {
                admission = coordinator._scheduler.BeginParticipating(state, _probe, owner, participant, Mode);
            }
            catch
            {
                // Nothing was published, so close the lease immediately rather than leaving an orphan for
                // another coordinator to prune (spec §10.3).
                _lease.Dispose();
                _lease = null;
                throw;
            }

            _ticket = admission.Ticket;
            _turnAction = admission.TurnAction;
            _turnStartedTick64 = admission.Admission == UiAdmission.GlobalWaiter
                ? null
                : TurnStartTick(state);
            _observedQueueDepth = InteractiveDesktopScheduler.CountLiveWaiters(state, _probe);
            coordinator._store.Publish(state);

            if (admission.Admission is UiAdmission.OwnerCommandWaiting or UiAdmission.GlobalWaiter)
            {
                _waitWatch.Start();
            }
        }

        /// <summary>
        /// Polls until this command's entry is <see cref="UiCommandStatus.Running"/> — covering both the
        /// global FIFO wait and the owner-local forward barrier. Cancellable and indefinite: there is no
        /// coordination timeout in v1 (spec §10.3, §10.4).
        /// </summary>
        private async Task WaitUntilRunnableAsync(CancellationToken cancellationToken)
        {
            if (!_waitWatch.IsRunning)
            {
                return;
            }

            var reporter = new UiCoordinationWaitReporter(
                coordinator._console, outputMode, participant.Operation);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                UiWaitDiagnostics diagnostics;
                using (var stateLock = coordinator._store.AcquireStateLock(cancellationToken))
                {
                    var read = coordinator._store.Read();
                    if (read.UnknownNewerVersion)
                    {
                        throw new UiCoordinationException(
                            UiCoordinationErrorCodes.Unavailable,
                            "UI turn coordination state was replaced by a newer version of winapp while this command was waiting.",
                            "Update winapp so every process on this desktop uses a compatible version, then retry.");
                    }

                    var state = read.State!;
                    if (coordinator._scheduler.Normalize(state, _probe) || read.RecoveredFromCorruption)
                    {
                        coordinator._store.Publish(state);
                    }

                    var entry = InteractiveDesktopScheduler.FindOwnerCommand(state, participant);
                    if (entry is { Status: UiCommandStatus.Running })
                    {
                        _waitWatch.Stop();
                        WaitedMs = _waitWatch.ElapsedMilliseconds;
                        Mode = entry.Mode;
                        _ticket = entry.Ticket;

                        // A global waiter only learns its turn's start once it has been promoted into
                        // ownerCommands, which may be many turns after it queued.
                        _turnStartedTick64 = TurnStartTick(state);
                        return;
                    }

                    diagnostics = BuildDiagnostics(state, entry);
                }

                reporter.ReportIfDue(_waitWatch.ElapsedMilliseconds, diagnostics);

                // Jittered so a burst of waiters does not resynchronize into a lock-step convoy on
                // state.lock. There are no heartbeat writes — a poll that finds nothing changed
                // publishes nothing.
                await coordinator._pollDelay
                    .DelayAsync(Random.Shared.Next(PollMinMs, PollMaxMs + 1), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private UiWaitDiagnostics BuildDiagnostics(InteractiveDesktopState state, OwnerCommandEntry? ownEntry)
        {
            var queueDepth = InteractiveDesktopScheduler.CountLiveWaiters(state, _probe);
            _observedQueueDepth = Math.Max(_observedQueueDepth, queueDepth);

            var active = state.OwnerCommands
                .Where(c => c.Status == UiCommandStatus.Running && c.Pid != participant.ProcessId)
                .OrderBy(c => c.Ticket ?? long.MaxValue)
                .FirstOrDefault();

            int commandsAhead;
            if (ownEntry is { Ticket: { } ownTicket })
            {
                // Owner-local: everything ahead of us in our own owner's barrier order.
                commandsAhead = state.OwnerCommands.Count(c => (c.Ticket ?? long.MaxValue) < ownTicket);
            }
            else if (_ticket is { } queuedTicket)
            {
                commandsAhead = state.OwnerCommands.Count
                    + state.Waiters.Count(w => w.Ticket < queuedTicket
                        && _probe.IsParticipantLive(w.Pid, w.ProcessStartTicksUtc));
            }
            else
            {
                commandsAhead = state.OwnerCommands.Count;
            }

            return new UiWaitDiagnostics(
                queueDepth,
                commandsAhead,
                active?.Pid,
                active?.Operation);
        }

        public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            // Serialize within this command first, then take the cross-process lock. Both are required:
            // the gate stops two concurrent tasks in this command from racing, and active.lock stops
            // other winapp processes from acting on the desktop at the same time.
            await _sectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                _activeLock = await coordinator
                    .AcquireActiveLockAsync(coordinator._paths.ActiveLockPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _sectionGate.Release();
                throw;
            }

            return new SectionScope(this);
        }

        private async Task ReleaseAllSectionsAsync()
        {
            // Safety net for a body that returned or threw without disposing its scope. Releasing the
            // file lock here (rather than the gate) is deliberate: a leaked gate would only stall this
            // already-finishing command, whereas a leaked active.lock would block the whole desktop
            // until the process exits.
            if (_activeLock is not null)
            {
                await _activeLock.DisposeAsync().ConfigureAwait(false);
                _activeLock = null;
            }
        }

        /// <summary>
        /// Removes this command's entry and applies the idle-grace rule, then closes the lease — in that
        /// order, so no entry is ever left without liveness proof (spec §9 rule 6, §10.6).
        /// </summary>
        private void Complete(bool renewGrace)
        {
            if (_lease is null)
            {
                return;
            }

            try
            {
                using var stateLock = coordinator._store.AcquireStateLock(CancellationToken.None);
                var read = coordinator._store.Read();
                if (read.State is { } state)
                {
                    coordinator._scheduler.CompleteCommand(state, _probe, participant, owner, renewGrace);
                    coordinator._store.Publish(state);
                }
            }
            catch (Exception ex) when (ex is UiCoordinationException or IOException)
            {
                // Teardown must never mask the command's own result. Windows deletes the lease below, so
                // the next coordinator prunes this entry anyway.
                coordinator._logger.LogDebug("UI coordination teardown could not update state: {Message}", ex.Message);
            }
            finally
            {
                _lease.Dispose();
                _lease = null;
            }
        }

        /// <summary>
        /// Emits the structured <c>cancelled</c> envelope (spec §14).
        /// </summary>
        /// <param name="cancelledWhileQueued">
        /// <see langword="true"/> when the command never reached execution, which is the case that also
        /// carries a queue position. <see langword="false"/> when it was cancelled after acquiring the
        /// turn, where UI side effects may already have happened.
        /// </param>
        private void EmitCancellation(bool cancelledWhileQueued)
        {
            var waitedMs = _waitWatch.ElapsedMilliseconds;
            int? queuePosition = null;

            try
            {
                using var stateLock = coordinator._store.AcquireStateLock(CancellationToken.None);
                var read = coordinator._store.Read();
                if (read.State is { } state && _ticket is { } ticket
                    && InteractiveDesktopScheduler.FindWaiter(state, participant) is not null)
                {
                    queuePosition = InteractiveDesktopScheduler.QueuePositionOf(state, _probe, ticket);
                }
            }
            catch (Exception ex) when (ex is UiCoordinationException or IOException)
            {
                coordinator._logger.LogDebug("Queue position could not be read while cancelling: {Message}", ex.Message);
            }

            var message = cancelledWhileQueued
                ? "UI turn wait was cancelled."
                : "The command was cancelled after it acquired the desktop; any UI changes it had already made remain.";

            UiJsonError.Emit(
                outputMode.Json,
                UiCoordinationErrorCodes.Cancelled,
                message,
                errorOut: parseResult.InvocationConfiguration.Error,
                coordination: new UiCoordinationInfo
                {
                    WaitedMs = waitedMs,
                    QueuePosition = queuePosition,
                });

            if (!outputMode.Json && !outputMode.Quiet)
            {
                if (cancelledWhileQueued)
                {
                    coordinator._logger.LogWarning(
                        "{Symbol} Cancelled while waiting {WaitedMs} ms for the desktop.",
                        UiSymbols.Warning,
                        waitedMs);
                }
                else
                {
                    coordinator._logger.LogWarning("{Symbol} {Message}", UiSymbols.Warning, message);
                }
            }
        }

        private void PublishTelemetry(bool completedNormally, UiCoordinationOutcome outcome)
        {
            var effectiveOutcome = _recoveredFromCorruption && completedNormally
                ? UiCoordinationOutcome.CorruptionRecovery
                : outcome;

            UiCoordinationTelemetryScope.Set(new UiCoordinationSummary(
                owner.Kind,
                Mode,
                _turnAction,
                effectiveOutcome,
                WaitedMs,
                _observedQueueDepth,
                MeasureTurnAgeMs()));
        }

        /// <summary>
        /// The turn's claim tick, or <see langword="null"/> when it is unknown. Zero is the "no turn"
        /// sentinel written by <c>CreateFresh</c> and by idle expiry, and is also what an older state
        /// file that predates the field deserializes to — measuring an age from it would report the
        /// machine's uptime.
        /// </summary>
        private static long? TurnStartTick(InteractiveDesktopState state)
            => state.TurnStartedTick64 == 0 ? null : state.TurnStartedTick64;

        /// <summary>
        /// How long the turn this command ran under had been held when the command finished. Zero for a
        /// detached observation and for a command that never acquired a turn, which have no turn to age.
        /// </summary>
        private long MeasureTurnAgeMs()
        {
            if (_turnStartedTick64 is not { } startedTick)
            {
                return 0;
            }

            // Clamped rather than trusted: the tick was written by whichever process claimed the turn,
            // and a state file that survived a reboot carries pre-restart ticks.
            var age = coordinator._clock.NowTicks64 - startedTick;
            return age > 0 ? age : 0;
        }

        /// <summary>
        /// Releases the in-process section gate once the command has finished.
        /// </summary>
        /// <remarks>
        /// The participant lease is normally closed by <see cref="Complete"/>, which must remove this
        /// command's state entry <em>before</em> the lease closes (spec §9 rule 6). This runs strictly
        /// after that, so the null-conditional call here is only a safety net for a lease that somehow
        /// outlived completion — it never inverts the ordering.
        /// </remarks>
        public void Dispose()
        {
            _lease?.Dispose();
            _lease = null;
            _sectionGate.Dispose();
        }

        private sealed class SectionScope(CoordinatedExecution execution) : IAsyncDisposable
        {
            private bool _disposed;

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                // Release the cross-process lock before the in-process gate, so the next waiter in this
                // command never finds the gate open while the file lock is still held.
                if (execution._activeLock is not null)
                {
                    await execution._activeLock.DisposeAsync().ConfigureAwait(false);
                    execution._activeLock = null;
                }

                execution._sectionGate.Release();
            }
        }
    }
}

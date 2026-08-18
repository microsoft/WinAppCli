// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// End-to-end coverage of <see cref="InteractiveDesktopLock"/> over the real store, leases and file
/// locks (issue #764): admission, the forward barrier, and the desktop-section contract.
/// </summary>
[TestClass]
[DoNotParallelize] // WINAPP_UI_LOCK_DIRECTORY and WINAPP_UI_OWNER_ID are process-wide.
public class InteractiveDesktopLockTests
{
    private string _lockDirectory = null!;
    private string? _previousLockOverride;
    private string? _previousOwnerId;
    private InteractiveDesktopPaths _paths = null!;
    private ParticipantRegistry _participants = null!;
    private InteractiveDesktopStateStore _store = null!;
    private InteractiveDesktopLock _coordinator = null!;

    [TestInitialize]
    public void Setup()
    {
        _lockDirectory = Path.Combine(Path.GetTempPath(), $"winapp-lock-svc-{Guid.NewGuid():N}");
        _previousLockOverride = Environment.GetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable);
        _previousOwnerId = Environment.GetEnvironmentVariable(UiOwnerResolver.OwnerIdVariable);

        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _lockDirectory);
        // A stable explicit owner keeps these tests independent of the test host's parent process.
        Environment.SetEnvironmentVariable(UiOwnerResolver.OwnerIdVariable, "interactive-desktop-lock-tests");

        var inspector = new ProcessInspector();
        _paths = new InteractiveDesktopPaths(inspector);
        _participants = new ParticipantRegistry(_paths, inspector, NullLogger<ParticipantRegistry>.Instance);
        _store = new InteractiveDesktopStateStore(
            _paths, _participants, new TickCountClock(), NullLogger<InteractiveDesktopStateStore>.Instance);
        _coordinator = new InteractiveDesktopLock(
            _store,
            _paths,
            _participants,
            new UiOwnerResolver(inspector),
            inspector,
            new TickCountClock(),
            new FakePollDelay(),
            new TestConsole(),
            NullLogger<InteractiveDesktopLock>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _previousLockOverride);
        Environment.SetEnvironmentVariable(UiOwnerResolver.OwnerIdVariable, _previousOwnerId);
        UiCoordinationTelemetryScope.Clear();

        try
        {
            if (Directory.Exists(_lockDirectory))
            {
                Directory.Delete(_lockDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test.
        }
    }

    private static ParseResult Parse()
    {
        var command = new Command("probe");
        command.Options.Add(WinAppRootCommand.JsonOption);
        command.Options.Add(WinAppRootCommand.QuietOption);
        command.Options.Add(WinAppRootCommand.VerboseOption);
        return command.Parse(["--quiet"]);
    }

    private Task<int> RunAsync(UiTurnMode mode, string operation, Func<IUiTurn, CancellationToken, Task<int>> body)
        => _coordinator.RunCoordinatedAsync(mode, operation, Parse(), body, CancellationToken.None);

    // ------------------------------------------------------------------------------- admission

    [TestMethod]
    public async Task ObserveOnAFreeDesktop_RunsDetachedAndLeavesNoState()
    {
        var ran = false;
        var exit = await RunAsync(UiTurnMode.Observe, "ui inspect", (turn, _) =>
        {
            ran = true;
            Assert.AreEqual(UiTurnMode.Observe, turn.Mode);
            return Task.FromResult(0);
        });

        Assert.AreEqual(0, exit);
        Assert.IsTrue(ran);
        Assert.IsFalse(_participants.AnyLiveParticipant(), "a detached observation opens no lease");
    }

    [TestMethod]
    public async Task DesktopExclusive_ClaimsTheTurnAndReleasesItOnCompletion()
    {
        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
        {
            using var stateLock = _store.AcquireStateLock(CancellationToken.None);
            var state = _store.Read().State!;
            Assert.IsNotNull(state.Owner, "the command must own the turn while it runs");
            Assert.AreEqual(1, state.OwnerCommands.Count);
            Assert.AreEqual(UiCommandStatus.Running, state.OwnerCommands[0].Status);
            return Task.FromResult(0);
        });

        using var afterLock = _store.AcquireStateLock(CancellationToken.None);
        var after = _store.Read().State!;
        Assert.AreEqual(0, after.OwnerCommands.Count, "the entry is removed on completion");
        Assert.IsFalse(_participants.AnyLiveParticipant(), "the lease closes after the entry is removed");
    }

    [TestMethod]
    public async Task NonZeroExitStillCountsAsACompletedCommand()
    {
        // Spec §10.6: a command that ran and returned a failing code still renews the grace, because the
        // workflow is alive and its next command is probably a retry.
        var exit = await RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(3));

        Assert.AreEqual(3, exit, "the command's own exit code must reach the caller unchanged");

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        Assert.IsNotNull(_store.Read().State!.Owner, "the turn stays reserved for the idle grace");
    }

    [TestMethod]
    public async Task CoordinationSummaryIsPublishedForTelemetry()
    {
        // Program opens this scope before invoking a command; do the same so the summary the coordinator
        // writes deep inside the invocation is visible here.
        UiCoordinationTelemetryScope.Begin();

        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0));

        var summary = UiCoordinationTelemetryScope.Current;
        Assert.IsNotNull(summary);
        Assert.AreEqual(UiOwnerKind.Explicit, summary!.IdentitySource);
        Assert.AreEqual(UiTurnMode.DesktopExclusive, summary.Mode);
        Assert.AreEqual(UiCoordinationOutcome.Completed, summary.Outcome);

        // Buckets only — an exact wait duration could correlate a user's workflow timing across events.
        StringAssert.Matches(summary.WaitBucket, new System.Text.RegularExpressions.Regex(@"^\d+(-\d+|\+)?$"));
    }

    // --------------------------------------------------------------------------- desktop sections

    [TestMethod]
    public async Task DesktopSection_TakesAndReleasesTheActiveLock()
    {
        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", async (turn, ct) =>
        {
            Assert.IsTrue(_store.IsActiveLockFree(), "active.lock is not held before the section opens");

            await using (await turn.EnterAsync(ct))
            {
                Assert.IsFalse(_store.IsActiveLockFree(), "the section must hold active.lock");
            }

            Assert.IsTrue(_store.IsActiveLockFree(), "the section must release active.lock on dispose");
            return 0;
        });
    }

    [TestMethod]
    public async Task DesktopSection_IsNotHeldAcrossTheWholeCommandBody()
    {
        // The turn wraps execution, but active.lock must not: output formatting, encoding and file
        // publication would otherwise block every other workflow for the whole command.
        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
        {
            Assert.IsTrue(_store.IsActiveLockFree());
            return Task.FromResult(0);
        });
    }

    [TestMethod]
    public async Task DesktopSection_SequentialEntersEachTakeTheLockAfresh()
    {
        // Screenshot and record open one section per restore/foreground/live-screen moment.
        await RunAsync(UiTurnMode.TurnShared, "ui screenshot", async (turn, ct) =>
        {
            for (var i = 0; i < 3; i++)
            {
                await using (await turn.EnterAsync(ct))
                {
                    Assert.IsFalse(_store.IsActiveLockFree());
                }

                Assert.IsTrue(_store.IsActiveLockFree());
            }

            return 0;
        });
    }

    [TestMethod]
    public async Task DesktopSection_ConcurrentEntersInOneCommandSerializeInsteadOfOverlapping()
    {
        // Regression guard: an earlier refcount design let a second concurrent task see depth > 0 and
        // skip active.lock entirely, running its desktop work with no cross-process protection.
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();

        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", async (turn, ct) =>
        {
            var tasks = Enumerable.Range(0, 8).Select(async _ =>
            {
                await using (await turn.EnterAsync(ct))
                {
                    lock (gate)
                    {
                        concurrent++;
                        maxConcurrent = Math.Max(maxConcurrent, concurrent);
                    }

                    Assert.IsFalse(_store.IsActiveLockFree(),
                        "every section must actually hold active.lock, not just believe it does");
                    await Task.Delay(5, ct);

                    lock (gate)
                    {
                        concurrent--;
                    }
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            return 0;
        });

        Assert.AreEqual(1, maxConcurrent, "unrelated concurrent enters in one command must serialize");
        Assert.IsTrue(_store.IsActiveLockFree());
    }

    [TestMethod]
    public async Task DesktopSection_IsReleasedWhenTheBodyThrows()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            RunAsync(UiTurnMode.DesktopExclusive, "ui click", async (turn, ct) =>
            {
                await turn.EnterAsync(ct);
                throw new InvalidOperationException("boom (test)");
            }));

        Assert.IsTrue(_store.IsActiveLockFree(),
            "a leaked section must never leave active.lock held for the rest of the process");
    }

    // ------------------------------------------------------------------------ coordination failure

    [TestMethod]
    public async Task UnknownNewerSchemaFailsParticipatingCommandsAndAllowsDetachedObservations()
    {
        _paths.EnsureDirectories();
        File.WriteAllText(
            _paths.StatePath,
            """{"version":99,"turnId":1,"nextTicket":2,"ownerCommands":[],"waiters":[]}""");

        var ex = await Assert.ThrowsExactlyAsync<UiCoordinationException>(() =>
            RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0)));
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);

        // Observations may continue: they never claim a turn and never write state.
        var observed = false;
        var exit = await RunAsync(UiTurnMode.Observe, "ui inspect", (_, _) =>
        {
            observed = true;
            return Task.FromResult(0);
        });

        Assert.AreEqual(0, exit);
        Assert.IsTrue(observed);
    }

    [TestMethod]
    public async Task InvalidExplicitOwnerIdFailsBeforeAnyUiSideEffect()
    {
        Environment.SetEnvironmentVariable(UiOwnerResolver.OwnerIdVariable, "   ");

        var ran = false;
        var ex = await Assert.ThrowsExactlyAsync<UiCoordinationException>(() =>
            RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
            {
                ran = true;
                return Task.FromResult(0);
            }));

        Assert.AreEqual(UiCoordinationErrorCodes.InvalidOwnerId, ex.Code);
        Assert.IsFalse(ran, "the command body must never run with an unusable owner identity");
        Assert.IsFalse(_participants.AnyLiveParticipant(), "no lease may be left behind");
    }

    // -------------------------------------------------------- queueing behind another live owner

    /// <summary>
    /// Publishes state in which a <em>different</em> owner holds the turn, and holds that owner's
    /// participant lease so it reads as live.
    /// </summary>
    /// <remarks>
    /// Participant identity is <c>(pid, processStartTicks)</c>, so a single process cannot legitimately
    /// act as two owners. Holding the foreign lease file <c>FileShare.None</c> reproduces exactly what
    /// the liveness probe observes for a real second process — a locked lease — without needing one,
    /// which keeps the cancellation contract deterministically testable. (Genuine cross-process
    /// behavior is covered separately by the multiprocess lane.)
    /// </remarks>
    private FileStream OccupyTurnWithAnotherOwner(int foreignPid = 424242, long foreignStart = 987654321)
    {
        _paths.EnsureDirectories();
        var leaseStream = new FileStream(
            _paths.LeasePath(foreignPid, foreignStart),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = InteractiveDesktopState.CreateFresh();
        state.TurnId = 1;
        state.NextTicket = 2;
        state.Owner = new OwnerRecord { Kind = UiOwnerKind.Explicit, Key = "some-other-workflow" };
        state.OwnerCommands.Add(new OwnerCommandEntry
        {
            Ticket = 1,
            Pid = foreignPid,
            ProcessStartTicksUtc = foreignStart,
            Operation = "ui click",
            Mode = UiTurnMode.DesktopExclusive,
            Status = UiCommandStatus.Running,
        });
        _store.Publish(state);

        return leaseStream;
    }

    [TestMethod]
    public async Task ACommandQueuesWhileAnotherOwnerHoldsTheTurn()
    {
        using var foreignLease = OccupyTurnWithAnotherOwner();

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = RunAsyncWithToken(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
        {
            ran = true;
            return Task.FromResult(0);
        }, cts.Token);

        await Task.Delay(250);
        Assert.IsFalse(ran, "the command must wait while another owner holds the turn");

        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            Assert.AreEqual(1, _store.Read().State!.Waiters.Count,
                "the command must be recorded as a global waiter");
        }

        await cts.CancelAsync();
        await queued;
    }

    [TestMethod]
    public async Task CancellingWhileQueuedExitsOneThirtyAndRemovesTheTicket()
    {
        using var foreignLease = OccupyTurnWithAnotherOwner();

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = RunAsyncWithToken(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
        {
            ran = true;
            return Task.FromResult(0);
        }, cts.Token);

        await Task.Delay(250);
        await cts.CancelAsync();

        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued,
            "a command cancelled while queued exits 130");
        Assert.IsFalse(ran, "it never reached execution, so it has no UI side effects");

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = _store.Read().State!;
        Assert.AreEqual(0, state.Waiters.Count, "cancellation must remove the waiter's ticket");
        Assert.AreEqual("some-other-workflow", state.Owner!.Key,
            "the cancelled command must not disturb the current owner");
    }

    [TestMethod]
    public async Task CancellingWhileQueuedEmitsTheStructuredCancelledError()
    {
        using var foreignLease = OccupyTurnWithAnotherOwner();

        var errorWriter = new StringWriter();
        var command = new Command("probe");
        command.Options.Add(WinAppRootCommand.JsonOption);
        command.Options.Add(WinAppRootCommand.QuietOption);
        command.Options.Add(WinAppRootCommand.VerboseOption);
        var parseResult = command.Parse(["--json"]);
        parseResult.InvocationConfiguration.Error = errorWriter;

        using var cts = new CancellationTokenSource();
        var queued = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", parseResult,
            (_, _) => Task.FromResult(0), cts.Token);

        await Task.Delay(250);
        await cts.CancelAsync();
        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued);

        var payload = errorWriter.ToString();
        StringAssert.Contains(payload, "\"code\":\"cancelled\"");
        StringAssert.Contains(payload, "\"waitedMs\"");
        // Owner identity must never surface, in raw or hashed form.
        Assert.IsFalse(payload.Contains("some-other-workflow", StringComparison.Ordinal));
        Assert.IsFalse(payload.Contains("interactive-desktop-lock-tests", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AQueuedCommandProceedsOnceTheOtherOwnerIsGone()
    {
        var foreignLease = OccupyTurnWithAnotherOwner();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = RunAsyncWithToken(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
        {
            started.SetResult();
            return Task.FromResult(0);
        }, CancellationToken.None);

        await Task.Delay(200);
        Assert.IsFalse(started.Task.IsCompleted);

        // Closing the lease is what Windows does when that process exits or is killed. A crash does not
        // renew the grace, so the turn is released immediately and this command is promoted.
        foreignLease.Dispose();

        Assert.AreEqual(0, await queued);
        Assert.IsTrue(started.Task.IsCompleted);
    }

    private Task<int> RunAsyncWithToken(
        UiTurnMode mode, string operation, Func<IUiTurn, CancellationToken, Task<int>> body, CancellationToken token)
        => _coordinator.RunCoordinatedAsync(mode, operation, Parse(), body, token);

    // ------------------------------------------------------ escalation must not swallow coordination

    [TestMethod]
    public async Task EscalationCancelledWhileQueuedExitsOneThirtyAndLeavesNoTrace()
    {
        // Exactly what `ui screenshot` does: an observational pass discovers it needs the foreground and
        // escalates. Here the escalation queues behind another owner and is cancelled.
        using var foreignLease = OccupyTurnWithAnotherOwner();
        var deadlineBefore = ReadOwnerDeadline();

        var errorWriter = new StringWriter();
        var parseResult = ParseWithWriter(errorWriter);

        using var cts = new CancellationTokenSource();
        var escalated = false;
        var queued = _coordinator.RunCoordinatedAsync(
            UiTurnMode.Observe, "ui screenshot", parseResult,
            async (turn, token) =>
            {
                await turn.EscalateToDesktopExclusiveAsync(token);
                escalated = true;
                return 0;
            },
            cts.Token);

        await Task.Delay(250);
        await cts.CancelAsync();

        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued,
            "a cancelled escalation is a cancellation, not an internal error");
        Assert.IsFalse(escalated, "the body never resumed, so it produced no image");
        StringAssert.Contains(errorWriter.ToString(), "\"code\":\"cancelled\"");

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = _store.Read().State!;
        Assert.AreEqual(0, state.Waiters.Count, "the escalation's ticket must be removed");
        Assert.AreEqual(deadlineBefore, state.IdleExpiresTick64,
            "a command cancelled while queued never ran, so it must renew no grace — neither its own "
                + "(it never held the turn) nor the current owner's");
    }

    [TestMethod]
    public async Task AnActiveRecordingThatFinalizesOnCancellationStillRenewsTheGrace()
    {
        // `ui record` observes Ctrl+C deliberately: it stops capturing, finalizes the MP4, and returns
        // success. That is a completed command, so the owner keeps its turn and its grace — the agent is
        // expected to issue a follow-up command next. "Completed normally" therefore means the body
        // returned, not that the token is unset.
        Assert.AreEqual(0, await RunAsync(UiTurnMode.TurnShared, "ui record", (_, _) => Task.FromResult(0)));
        var deadlineBeforeRecording = ReadOwnerDeadline();

        await Task.Delay(50);

        using var cts = new CancellationTokenSource();
        var bodyObservedCancellation = false;
        var exitCode = await RunAsyncWithToken(UiTurnMode.TurnShared, "ui record", async (_, token) =>
        {
            // Stands in for a capture loop that is interrupted and finalizes its output.
            await cts.CancelAsync();
            bodyObservedCancellation = token.IsCancellationRequested;
            return 0;
        }, cts.Token);

        Assert.AreEqual(0, exitCode, "a finalized recording reports success");
        Assert.IsTrue(bodyObservedCancellation,
            "the recording must actually observe cancellation, or this test proves nothing");
        Assert.IsTrue(ReadOwnerDeadline() > deadlineBeforeRecording,
            "a recording that finalized and returned successfully must renew its owner's idle grace");
    }

    [TestMethod]
    public async Task EscalationAgainstUnknownNewerStateReportsCoordinationUnavailable()
    {
        _paths.EnsureDirectories();
        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var state = InteractiveDesktopState.CreateFresh();
            state.Version = int.MaxValue;
            _store.Publish(state);
        }

        var ex = await Assert.ThrowsExactlyAsync<UiCoordinationException>(() =>
            RunAsync(UiTurnMode.Observe, "ui screenshot", async (turn, token) =>
            {
                await turn.EscalateToDesktopExclusiveAsync(token);
                return 0;
            }));

        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code,
            "escalating against state written by a newer build must fail closed, not run uncoordinated");
    }

    private long ReadOwnerDeadline()
    {
        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        return _store.Read().State!.IdleExpiresTick64;
    }

    private static ParseResult ParseWithWriter(TextWriter errorWriter)
    {
        var command = new Command("probe");
        command.Options.Add(WinAppRootCommand.JsonOption);
        command.Options.Add(WinAppRootCommand.QuietOption);
        command.Options.Add(WinAppRootCommand.VerboseOption);
        var parseResult = command.Parse(["--json"]);
        parseResult.InvocationConfiguration.Error = errorWriter;
        return parseResult;
    }

    // ------------------------------------------------- ownership lapsing mid-registration (observe)

    /// <summary>
    /// The turn is lost between the owner check and the admission that follows it.
    /// </summary>
    /// <remarks>
    /// <see cref="InteractiveDesktopScheduler.BeginObserve"/> normalizes again, so an owner that was
    /// current a moment earlier can be gone by the time admission runs — here because the shell the
    /// reservation was derived from exits in between. The observation must then run fully detached:
    /// keeping the lease open would publish liveness for a participant with no state entry, and
    /// completing later would adjust a different owner's turn.
    /// </remarks>
    [TestMethod]
    public async Task ObserveWhoseOwnerLapsesDuringAdmissionRunsFullyDetached()
    {
        const int parentPid = 4242;
        const long parentStart = 777_777;

        // Parent-derived ownership, so the fake inspector controls exactly when the turn lapses.
        Environment.SetEnvironmentVariable(UiOwnerResolver.OwnerIdVariable, null);
        var inspector = new ParentDiesAfterFirstProbeInspector(parentPid, parentStart);
        var paths = new InteractiveDesktopPaths(inspector);
        var participants = new ParticipantRegistry(paths, inspector, NullLogger<ParticipantRegistry>.Instance);
        var store = new InteractiveDesktopStateStore(
            paths, participants, new TickCountClock(), NullLogger<InteractiveDesktopStateStore>.Instance);
        var coordinator = new InteractiveDesktopLock(
            store, paths, participants, new UiOwnerResolver(inspector), inspector,
            new TickCountClock(), new FakePollDelay(), new TestConsole(),
            NullLogger<InteractiveDesktopLock>.Instance);

        paths.EnsureDirectories();
        using (var stateLock = store.AcquireStateLock(CancellationToken.None))
        {
            var state = InteractiveDesktopState.CreateFresh();
            state.TurnId = 3;
            state.Owner = new OwnerRecord
            {
                Kind = UiOwnerKind.Parent,
                Key = UiOwnerResolver.ComputeParentKey(parentPid, parentStart),
                DiagnosticParentPid = parentPid,
                ParentStartTicksUtc = parentStart,
            };
            state.IdleExpiresTick64 = new TickCountClock().NowTicks64 + InteractiveDesktopScheduler.IdleGraceMs;
            store.Publish(state);
        }

        var leaseLiveDuringBody = true;
        var exitCode = await coordinator.RunCoordinatedAsync(
            UiTurnMode.Observe, "ui inspect", Parse(),
            (_, _) =>
            {
                // The decisive check. By teardown the lease is closed either way, so the only moment the
                // bug is observable is while the detached body runs: a detached observation must hold no
                // lease at all, or other processes see liveness for a participant that has no entry.
                leaseLiveDuringBody = participants.AnyLiveParticipant();
                return Task.FromResult(0);
            },
            CancellationToken.None);

        Assert.AreEqual(0, exitCode, "a detached observation still runs; it simply pins nothing");
        Assert.IsTrue(
            inspector.ParentProbes >= 2,
            "the test is only meaningful if the owner check and the admission both probed the parent");
        Assert.IsFalse(
            leaseLiveDuringBody,
            "the lease opened before admission must be closed as soon as the admission came back detached");

        using (var stateLock = store.AcquireStateLock(CancellationToken.None))
        {
            var state = store.Read().State!;
            Assert.AreEqual(0, state.OwnerCommands.Count,
                "a detached observation must publish no owner-command entry");
        }

        Assert.IsFalse(participants.AnyLiveParticipant(),
            "the lease must also be gone once the command has finished");
    }

    /// <summary>
    /// Reports the owning shell as alive for the first probe and gone afterwards, so a turn lapses at a
    /// precisely known point instead of depending on wall-clock timing.
    /// </summary>
    private sealed class ParentDiesAfterFirstProbeInspector(int parentPid, long parentStartTicks) : IProcessInspector
    {
        private readonly ProcessInspector _real = new();

        public int ParentProbes { get; private set; }

        public int CurrentProcessId => _real.CurrentProcessId;

        public long CurrentProcessStartTicksUtc => _real.CurrentProcessStartTicksUtc;

        public int CurrentSessionId => _real.CurrentSessionId;

        public int? TryGetParentProcessId() => parentPid;

        public long? TryGetProcessStartTicksUtc(int processId)
            => processId == parentPid ? parentStartTicks : _real.TryGetProcessStartTicksUtc(processId);

        public bool? IsProcessAlive(int processId, long startTicksUtc)
        {
            if (processId != parentPid || startTicksUtc != parentStartTicks)
            {
                return _real.IsProcessAlive(processId, startTicksUtc);
            }

            ParentProbes++;
            return ParentProbes <= 1;
        }
    }
}
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

}
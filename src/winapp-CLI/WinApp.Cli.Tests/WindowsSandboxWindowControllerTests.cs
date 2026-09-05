// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// How winapp decides which Windows Sandbox client window is the one it manages.
/// </summary>
/// <remarks>
/// The rules matter because Windows routinely leaves earlier <c>WindowsSandboxRemoteSession</c>
/// processes behind, and the user may have opened a Sandbox of their own. Choosing wrongly parks a
/// stranger's window off-screen, or captures someone else's desktop and reports it as this
/// target's — both of which look exactly like success.
/// </remarks>
[TestClass]
public class WindowsSandboxWindowControllerTests
{
    [TestMethod]
    public void SelectNewClient_IgnoresPreexistingAndHandlelessProcesses()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectNewClient(
            new HashSet<int> { 10, 11 },
            [
                Client(10, 100),
                Client(12, nint.Zero),
                Client(13, 300),
            ]);

        Assert.IsFalse(ambiguous);
        Assert.IsNotNull(client);
        Assert.AreEqual((nint)300, client.Handle);
        Assert.AreEqual(13, client.ProcessId);
    }

    [TestMethod]
    public void SelectNewClient_SelectsNothingWhenNoWindowAppeared()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectNewClient(
            new HashSet<int> { 10 },
            [Client(10, 100)]);

        Assert.IsNull(client);
        Assert.IsFalse(ambiguous);
    }

    [TestMethod]
    public void SelectNewClient_RefusesToGuessBetweenTwoNewWindows()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectNewClient(
            new HashSet<int> { 10 },
            [
                Client(12, 200),
                Client(13, 300),
            ]);

        Assert.IsNull(client, "Parking a window that may belong to someone else is worse than parking nothing.");
        Assert.IsTrue(ambiguous);
    }

    [TestMethod]
    public void ResolveClient_PrefersTheRecordedWindowWhileItIsStillOpen()
    {
        var recorded = Client(13, 300);

        var resolved = WindowsSandboxWindowController.ResolveClient(
            recorded,
            [Client(12, 200), recorded]);

        Assert.AreEqual(recorded, resolved);
    }

    [TestMethod]
    public void ResolveClient_IgnoresARecordedWindowThatIsNoLongerOpen()
    {
        // Windows recycles both handles and process IDs, so the recorded window is only the same
        // window while its start time matches too.
        var resolved = WindowsSandboxWindowController.ResolveClient(
            Client(13, 300, startTicksUtc: 1000),
            [Client(13, 300, startTicksUtc: 5000)]);

        Assert.AreEqual(5000, resolved.StartTicksUtc);
    }

    [TestMethod]
    public void ResolveClient_AdoptsTheOnlyOpenWindowWhenNothingWasRecorded()
    {
        var resolved = WindowsSandboxWindowController.ResolveClient(remembered: null, [Client(13, 300)]);

        Assert.AreEqual((nint)300, resolved.Handle);
    }

    [TestMethod]
    public void ResolveClient_FailsWhenNoClientWindowIsOpen()
    {
        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxWindowController.ResolveClient(remembered: null, []));

        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, ex.Error.Code);
        Assert.IsNotNull(ex.Error.UserAction);
    }

    [TestMethod]
    public void ResolveClient_FailsWhenSeveralWindowsAreOpenAndNoneWasRecorded()
    {
        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxWindowController.ResolveClient(
                remembered: null,
                [Client(12, 200), Client(13, 300)]));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, ex.Error.Code);
        Assert.IsNotNull(ex.Error.Context);
        Assert.AreEqual("12,13", ex.Error.Context["clientProcessIds"]);
    }

    [TestMethod]
    public void ResolveClient_FailsWhenTheRecordedWindowIsGoneAndOthersRemain()
    {
        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxWindowController.ResolveClient(
                Client(11, 100),
                [Client(12, 200), Client(13, 300)]));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, ex.Error.Code);
    }

    // ---- claiming the window a connect created ---------------------------------------

    [TestMethod]
    public async Task PlaceConnectedClient_ParksTheNewWindowOnceItHasStayedTheOnlyOne()
    {
        var scripted = new ScriptedDesktop([Client(11, 100)]);
        scripted.Add([Client(11, 100), Client(12, 200)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(11), TestContext.CancellationToken);

        Assert.IsNotNull(placed);
        Assert.AreEqual((nint)200, placed.Handle);
        CollectionAssert.AreEqual(new nint[] { 200 }, scripted.Parked);
    }

    /// <summary>
    /// Two <c>wsb connect</c> calls a few milliseconds apart both create a client, but the second
    /// process is not visible on the poll that sees the first. Claiming on that poll is what parks
    /// and persists the other caller's window as winapp's own.
    /// </summary>
    [TestMethod]
    public async Task PlaceConnectedClient_AnotherConnectsClientAppearsWhileConfirming_ClaimsNothing()
    {
        var scripted = new ScriptedDesktop([Client(11, 100)]);
        scripted.Add([Client(11, 100), Client(12, 200)]);
        scripted.Add([Client(11, 100), Client(12, 200)]);
        scripted.Add([Client(11, 100), Client(12, 200), Client(13, 300)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(11), TestContext.CancellationToken);

        Assert.IsNull(placed, "winapp cannot tell which client is its own, so it claims neither.");
        Assert.AreEqual(0, scripted.Parked.Count, "Another caller's window must never be moved off-screen.");
    }

    [TestMethod]
    public async Task PlaceConnectedClient_TheCandidateClosesWhileConfirming_ClaimsNothing()
    {
        var scripted = new ScriptedDesktop([Client(11, 100)]);
        scripted.Add([Client(11, 100), Client(12, 200)]);
        scripted.Add([Client(11, 100)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(11), TestContext.CancellationToken);

        Assert.IsNull(placed, "A handle that is already gone is worse than no handle at all.");
        Assert.AreEqual(0, scripted.Parked.Count);
    }

    [TestMethod]
    public async Task PlaceConnectedClient_TwoNewWindowsOnTheFirstLook_ClaimsNothingImmediately()
    {
        var scripted = new ScriptedDesktop([Client(11, 100), Client(12, 200), Client(13, 300)]);
        var controller = scripted.CreateController();

        Assert.IsNull(await controller.PlaceConnectedClientAsync(
            Snapshot(11), TestContext.CancellationToken));
        Assert.AreEqual(0, scripted.Parked.Count);
        Assert.AreEqual(1, scripted.Looks, "There is nothing to confirm, so no time is spent confirming.");
    }

    [TestMethod]
    public async Task PlaceConnectedClient_NoWindowEverAppears_GivesUpWithoutClaimingAnything()
    {
        var scripted = new ScriptedDesktop([Client(11, 100)]);
        var controller = scripted.CreateController();

        Assert.IsNull(await controller.PlaceConnectedClientAsync(
            Snapshot(11), TestContext.CancellationToken));
        Assert.AreEqual(0, scripted.Parked.Count);
    }

    public TestContext TestContext { get; set; } = null!;

    private static WindowsSandboxWindowSnapshot Snapshot(params int[] existingProcessIds) =>
        new(new HashSet<int>(existingProcessIds), default);

    private static SandboxClientWindow Client(int processId, nint handle, long startTicksUtc = 1000) =>
        new(handle, processId, startTicksUtc);

    /// <summary>
    /// A desktop whose client windows change on a script, with time advancing only when the
    /// controller waits, so a race that lasts milliseconds in production is reproducible here.
    /// </summary>
    private sealed class ScriptedDesktop
    {
        private readonly List<IReadOnlyList<SandboxClientWindow>> _looks = [];
        private DateTimeOffset _now = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public ScriptedDesktop(IReadOnlyList<SandboxClientWindow> initial) => _looks.Add(initial);

        /// <summary>What the next look at the desktop returns; the last entry then repeats.</summary>
        public void Add(IReadOnlyList<SandboxClientWindow> clients) => _looks.Add(clients);

        public List<nint> Parked { get; } = [];

        public int Looks { get; private set; }

        public WindowsSandboxWindowController CreateController()
        {
            var controller = new WindowsSandboxWindowController(
                Look,
                (client, _) => Parked.Add(client.Handle))
            {
                UtcNow = () => _now,
            };

            controller.Delay = (delay, _) =>
            {
                _now += delay;
                return Task.CompletedTask;
            };

            return controller;
        }

        private IReadOnlyList<SandboxClientWindow> Look()
        {
            var index = Math.Min(Looks, _looks.Count - 1);
            Looks++;
            return _looks[index];
        }
    }
}

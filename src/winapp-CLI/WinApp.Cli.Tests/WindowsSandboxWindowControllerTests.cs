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

    private static SandboxClientWindow Client(int processId, nint handle, long startTicksUtc = 1000) =>
        new(handle, processId, startTicksUtc);
}

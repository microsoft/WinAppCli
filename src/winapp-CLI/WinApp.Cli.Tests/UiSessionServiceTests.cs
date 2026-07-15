// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Direct unit tests for <see cref="UiSessionService"/>. All OS boundaries (process enumeration and
/// Win32 window queries) are driven through <see cref="FakeSystemUiQuery"/>, and window discovery
/// through <see cref="FakeUiAutomationService"/>, so every resolver branch is exercised
/// deterministically without a live desktop or specific running processes.
/// </summary>
[TestClass]
public class UiSessionServiceTests
{
    private static (UiSessionService Service, FakeUiAutomationService Uia, FakeSystemUiQuery Sys) NewService()
    {
        var uia = new FakeUiAutomationService();
        var sys = new FakeSystemUiQuery();
        var service = new UiSessionService(uia, sys, NullLogger<UiSessionService>.Instance);
        return (service, uia, sys);
    }

    [TestMethod]
    public void UiSessionInfo_IsExplicitWindow_DefaultsToFalse()
    {
        var info = new UiSessionInfo();
        Assert.IsFalse(info.IsExplicitWindow);
    }

    // ---- HWND resolution -------------------------------------------------

    [TestMethod]
    public async Task ResolveByHwnd_WindowNotFound_Throws()
    {
        var (service, _, sys) = NewService();
        sys.ProcessIdForWindowResult = 0; // window not found / not accessible

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveSessionAsync(app: null, hwnd: 0x9999, CancellationToken.None));

        StringAssert.Contains(ex.Message, "not found or not accessible");
    }

    [TestMethod]
    public async Task ResolveByHwnd_Found_SetsExplicitAndTitleFromWindowText()
    {
        var (service, _, sys) = NewService();
        sys.ProcessIdForWindowResult = 4321;
        sys.ProcessesById[4321] = new UiProcessInfo(4321, "notepad", 0, null);
        sys.WindowTextResult = "Untitled - Notepad";

        var session = await service.ResolveSessionAsync(app: null, hwnd: 0x1234, CancellationToken.None);

        Assert.IsTrue(session.IsExplicitWindow,
            "Sessions resolved via --window must be marked explicit so inspect/search/find don't expand to other windows (#472).");
        Assert.AreEqual((long)0x1234, session.WindowHandle);
        Assert.AreEqual(4321, session.ProcessId);
        Assert.AreEqual("notepad", session.ProcessName);
        Assert.AreEqual("Untitled - Notepad", session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByHwnd_Found_NoWindowText_LeavesTitleNull()
    {
        var (service, _, sys) = NewService();
        sys.ProcessIdForWindowResult = 10;
        sys.DefaultProcessById = new UiProcessInfo(0, "proc", 0, null);
        sys.WindowTextResult = null; // empty/unavailable title

        var session = await service.ResolveSessionAsync(app: null, hwnd: 0x1000, CancellationToken.None);

        Assert.IsTrue(session.IsExplicitWindow);
        Assert.AreEqual("proc", session.ProcessName);
        Assert.IsNull(session.WindowTitle);
    }

    // ---- Missing selector ------------------------------------------------

    [TestMethod]
    public async Task ResolveSession_NoAppNoWindow_Throws()
    {
        var (service, _, _) = NewService();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveSessionAsync(app: null, hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Specify --app");
    }

    [TestMethod]
    public async Task ResolveSession_WhitespaceApp_ZeroHwnd_Throws()
    {
        var (service, _, _) = NewService();

        // hwnd 0 is not "> 0", so it falls through to the app check.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveSessionAsync(app: "   ", hwnd: 0, CancellationToken.None));
    }

    // ---- PID resolution --------------------------------------------------

    [TestMethod]
    public async Task ResolveByPid_ProcessNotFound_Throws()
    {
        var (service, _, _) = NewService();

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveSessionAsync(app: "999999", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "No process found with PID 999999");
    }

    [TestMethod]
    public async Task ResolveByPid_Found_NoWindows_UsesMainWindowTitle()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[500] = new UiProcessInfo(500, "myapp", 0, "Main Title");
        uia.WindowsByPidResult = []; // no discoverable windows

        var session = await service.ResolveSessionAsync(app: "500", hwnd: null, CancellationToken.None);

        Assert.IsFalse(session.IsExplicitWindow,
            "Only --window should mark a session as explicit; --app/PID resolution must leave it false.");
        Assert.AreEqual(500, session.ProcessId);
        Assert.AreEqual("myapp", session.ProcessName);
        Assert.AreEqual("Main Title", session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_NoWindows_EmptyMainTitle_YieldsNullTitle()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[501] = new UiProcessInfo(501, "myapp", 0, "");
        uia.WindowsByPidResult = [];

        var session = await service.ResolveSessionAsync(app: "501", hwnd: null, CancellationToken.None);

        Assert.IsNull(session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_SingleWindow_CreatesSession()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[600] = new UiProcessInfo(600, "app6", 0, null);
        uia.WindowsByPidResult = [((nint)0xAAA, 600, "Win6")];

        var session = await service.ResolveSessionAsync(app: "600", hwnd: null, CancellationToken.None);

        Assert.AreEqual(600, session.ProcessId);
        Assert.AreEqual("app6", session.ProcessName);
        Assert.AreEqual((long)0xAAA, session.WindowHandle);
        Assert.AreEqual("Win6", session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_MultipleWindows_AutoSelectsForeground()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[700] = new UiProcessInfo(700, "app7", 0, null);
        uia.WindowsByPidResult = [((nint)0x100, 700, "A"), ((nint)0x200, 700, "B")];
        sys.ForegroundWindowResult = 0x200; // the second window is foreground

        var session = await service.ResolveSessionAsync(app: "700", hwnd: null, CancellationToken.None);

        Assert.AreEqual((long)0x200, session.WindowHandle);
        Assert.AreEqual("B", session.WindowTitle);
        Assert.AreEqual("app7", session.ProcessName);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_MultipleWindows_AutoSelectsLargest_WhenNoForeground()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[701] = new UiProcessInfo(701, "app7b", 0, null);
        uia.WindowsByPidResult = [((nint)0x111, 701, "AA"), ((nint)0x222, 701, "BB")];
        sys.ForegroundWindowResult = 0; // no foreground → "largest" heuristic
        // Distinct areas so "largest" has a single correct answer: 0x222 (300×300) dwarfs 0x111 (100×100).
        sys.WindowSizeByHwnd[0x111] = (100, 100);
        sys.WindowSizeByHwnd[0x222] = (300, 300);

        var session = await service.ResolveSessionAsync(app: "701", hwnd: null, CancellationToken.None);

        Assert.AreEqual(701, session.ProcessId);
        Assert.AreEqual("app7b", session.ProcessName);
        Assert.AreEqual(0x222L, session.WindowHandle,
            "Auto-select must pick the largest-area window (0x222), not just any candidate.");
    }

    // ---- Exact process-name resolution ----------------------------------

    [TestMethod]
    public async Task ResolveByName_ExactSingle_ReturnsProcess()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult = [new UiProcessInfo(800, "calc", 0, null)];
        uia.WindowsByPidResult = [];

        var session = await service.ResolveSessionAsync(app: "calc", hwnd: null, CancellationToken.None);

        Assert.AreEqual(800, session.ProcessId);
        Assert.AreEqual("calc", session.ProcessName);
        Assert.IsNull(session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByName_ExactMultiple_OneWithWindow_ReturnsThatOne()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult =
        [
            new UiProcessInfo(810, "dup", 0, null),          // no window
            new UiProcessInfo(811, "dup", 0x900, "Real"),    // has a window
        ];
        uia.WindowsByPidResult = [];

        var session = await service.ResolveSessionAsync(app: "dup", hwnd: null, CancellationToken.None);

        Assert.AreEqual(811, session.ProcessId);
        Assert.AreEqual("Real", session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByName_ExactMultiple_MultipleWithWindow_Throws()
    {
        var (service, _, sys) = NewService();
        sys.ByNameResult =
        [
            new UiProcessInfo(820, "ambig", 0x1, "W1"),
            new UiProcessInfo(821, "ambig", 0x2, "W2"),
        ];

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveSessionAsync(app: "ambig", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Multiple 'ambig' windows found");
        StringAssert.Contains(ex.Message, "PID 820");
        StringAssert.Contains(ex.Message, "PID 821");
    }

    [TestMethod]
    public async Task ResolveByName_ExactMultiple_NoneWithWindow_FallsToPartialMatch()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult =
        [
            new UiProcessInfo(830, "xy", 0, null),
            new UiProcessInfo(831, "xy", 0, ""),
        ];
        sys.MatchingResult = [new UiProcessInfo(832, "xyz", 0, null)];
        uia.WindowsByPidResult = [];

        var session = await service.ResolveSessionAsync(app: "xy", hwnd: null, CancellationToken.None);

        Assert.AreEqual(832, session.ProcessId);
        Assert.AreEqual("xyz", session.ProcessName);
    }

    // ---- Partial process-name resolution --------------------------------

    [TestMethod]
    public async Task ResolveByName_PartialMultiple_OneWithWindow_ReturnsWithLog()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult = []; // no exact match
        sys.MatchingResult =
        [
            new UiProcessInfo(850, "pob", 0, null),
            new UiProcessInfo(851, "poc", 0xA, "Poc Win"),
        ];
        uia.WindowsByPidResult = [];

        var session = await service.ResolveSessionAsync(app: "po", hwnd: null, CancellationToken.None);

        Assert.AreEqual(851, session.ProcessId);
        Assert.AreEqual("poc", session.ProcessName);
        Assert.AreEqual("Poc Win", session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByName_PartialMultiple_MultipleWithWindow_Throws()
    {
        var (service, _, sys) = NewService();
        sys.MatchingResult =
        [
            new UiProcessInfo(860, "mpa", 0x1, "MW1"),
            new UiProcessInfo(861, "mpb", 0x2, "MW2"),
        ];

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveSessionAsync(app: "mp", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Multiple processes matching 'mp' found");
        StringAssert.Contains(ex.Message, "PID 860 (mpa)");
        StringAssert.Contains(ex.Message, "PID 861 (mpb)");
    }

    [TestMethod]
    public async Task ResolveByName_PartialMultiple_NoneWithWindow_FallsToTitleSearch()
    {
        var (service, uia, sys) = NewService();
        sys.MatchingResult =
        [
            new UiProcessInfo(870, "pwa", 0, null),
            new UiProcessInfo(871, "pwb", 0, ""),
        ];
        uia.WindowsByTitleResult = []; // title search also finds nothing → throws

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveSessionAsync(app: "pw", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "No running app found matching 'pw'");
    }

    // ---- Title-search fallback ------------------------------------------

    [TestMethod]
    public async Task ResolveByTitle_NoWindows_Throws()
    {
        var (service, uia, _) = NewService();
        uia.WindowsByTitleResult = [];

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveSessionAsync(app: "ghost", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "No running app found matching 'ghost'");
    }

    [TestMethod]
    public async Task ResolveByTitle_SingleWindow_KnownProcessName()
    {
        var (service, uia, sys) = NewService();
        uia.WindowsByTitleResult = [((nint)0xB, 900, "Ghost Win")];
        sys.DefaultProcessById = new UiProcessInfo(0, "ghostapp", 0, null);

        var session = await service.ResolveSessionAsync(app: "ghost", hwnd: null, CancellationToken.None);

        Assert.AreEqual(900, session.ProcessId);
        Assert.AreEqual((long)0xB, session.WindowHandle);
        Assert.AreEqual("Ghost Win", session.WindowTitle);
        Assert.AreEqual("ghostapp", session.ProcessName);
    }

    [TestMethod]
    public async Task ResolveByTitle_SingleWindow_UnknownProcessName_WhenLookupFails()
    {
        var (service, uia, _) = NewService();
        uia.WindowsByTitleResult = [((nint)0xE, 960, "GW")];
        // No default and no seed for PID 960 → CreateSession's name lookup returns null → "Unknown".

        var session = await service.ResolveSessionAsync(app: "ghost2", hwnd: null, CancellationToken.None);

        Assert.AreEqual(960, session.ProcessId);
        Assert.AreEqual("Unknown", session.ProcessName);
        Assert.AreEqual("GW", session.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByTitle_MultipleWindows_AutoSelects()
    {
        var (service, uia, sys) = NewService();
        uia.WindowsByTitleResult = [((nint)0xC1, 910, "G1"), ((nint)0xC2, 910, "G2")];
        sys.ForegroundWindowResult = 0; // → largest heuristic
        sys.DefaultProcessById = new UiProcessInfo(0, "multi", 0, null);
        // 0xC1 (400×400) is unambiguously larger than 0xC2 (50×50) → it must win.
        sys.WindowSizeByHwnd[0xC1] = (400, 400);
        sys.WindowSizeByHwnd[0xC2] = (50, 50);

        var session = await service.ResolveSessionAsync(app: "ghosts", hwnd: null, CancellationToken.None);

        Assert.AreEqual(910, session.ProcessId);
        Assert.AreEqual("multi", session.ProcessName);
        Assert.AreEqual(0xC1L, session.WindowHandle,
            "Auto-select must pick the largest-area window (0xC1).");
    }

    // ---- ClassifyWindow (pure) ------------------------------------------

    [TestMethod]
    public void ClassifyWindow_NullClassName_ReturnsWindow()
        => Assert.AreEqual("window", UiSessionService.ClassifyWindow(null));

    [TestMethod]
    public void ClassifyWindow_PopupClass_ReturnsPopup()
        => Assert.AreEqual("popup", UiSessionService.ClassifyWindow("SomePopupClass"));

    [TestMethod]
    public void ClassifyWindow_DialogClass_ReturnsDialog()
        => Assert.AreEqual("dialog", UiSessionService.ClassifyWindow("#32770"));

    [TestMethod]
    public void ClassifyWindow_OrdinaryClass_ReturnsWindow()
        => Assert.AreEqual("window", UiSessionService.ClassifyWindow("Button"));
}

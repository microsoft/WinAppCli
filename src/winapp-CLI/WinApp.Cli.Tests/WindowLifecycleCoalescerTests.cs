// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowLifecycleCoalescerTests
{
    private const long Ms = TimeSpan.TicksPerMillisecond;

    [TestMethod]
    public void CreatePlusShow_ForSameWindow_EmitsOnce()
    {
        var c = new WindowLifecycleCoalescer();
        const long hwnd = 0x1000;

        // EVENT_OBJECT_CREATE then EVENT_OBJECT_SHOW both map to window-open for the same hwnd.
        Assert.IsTrue(c.ShouldEmit(hwnd, isOpen: true, nowTicks: 0), "first open should emit");
        Assert.IsFalse(c.ShouldEmit(hwnd, isOpen: true, nowTicks: 5 * Ms), "paired SHOW should be suppressed");
    }

    [TestMethod]
    public void DestroyPlusHide_ForSameWindow_EmitsOnce()
    {
        var c = new WindowLifecycleCoalescer();
        const long hwnd = 0x2000;

        Assert.IsTrue(c.ShouldEmit(hwnd, isOpen: false, nowTicks: 0), "first close should emit");
        Assert.IsFalse(c.ShouldEmit(hwnd, isOpen: false, nowTicks: 5 * Ms), "paired HIDE should be suppressed");
    }

    [TestMethod]
    public void OpenThenClose_EmitsBothTransitions()
    {
        var c = new WindowLifecycleCoalescer();
        const long hwnd = 0x3000;

        Assert.IsTrue(c.ShouldEmit(hwnd, isOpen: true, nowTicks: 0));
        Assert.IsFalse(c.ShouldEmit(hwnd, isOpen: true, nowTicks: 1 * Ms));   // coalesced open
        Assert.IsTrue(c.ShouldEmit(hwnd, isOpen: false, nowTicks: 2 * Ms));   // close is a new transition
        Assert.IsFalse(c.ShouldEmit(hwnd, isOpen: false, nowTicks: 3 * Ms));  // coalesced close
    }

    [TestMethod]
    public void DifferentWindows_AreTrackedIndependently()
    {
        var c = new WindowLifecycleCoalescer();

        Assert.IsTrue(c.ShouldEmit(0xAAAA, isOpen: true, nowTicks: 0));
        Assert.IsTrue(c.ShouldEmit(0xBBBB, isOpen: true, nowTicks: 1 * Ms),
            "a different window's open must not be coalesced against another window");
    }

    [TestMethod]
    public void SameKind_AfterCoalescingWindow_EmitsAgain()
    {
        var c = new WindowLifecycleCoalescer(windowTicks: 100 * Ms);
        const long hwnd = 0x4000;

        Assert.IsTrue(c.ShouldEmit(hwnd, isOpen: true, nowTicks: 0));
        // Well outside the coalescing window — treat as a genuine second open (e.g. re-shown window).
        Assert.IsTrue(c.ShouldEmit(hwnd, isOpen: true, nowTicks: 500 * Ms));
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Screenshot verb coverage: single-window capture (JSON + non-JSON), multi-window discovery and
/// compositing (by-PID, by-name, owned-dialog), capture-failure, window-handle edge cases, and the
/// COM/generic error branches. All file-writing paths target a temp path so no PNG lands in the cwd.
/// Multi-window discovery is driven entirely through the fakes (FindWindowsByPid / IOwnedWindowFinder)
/// so no real desktop window is required.
/// </summary>
public partial class UiCommandTests
{
    private string ShotPath() => Path.Combine(_tempDirectory.FullName, "shot.png");

    [TestMethod]
    public async Task Screenshot_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Screenshot_SingleWindow_NonJson_WritesFile()
    {
        // Selector present → multi-window discovery is skipped; single element crop path (logs, no JSON).
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp", "-o", path]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(path), "expected a PNG to be written to the output path");
    }

    [TestMethod]
    public async Task Screenshot_MultiWindowByPid_Json_EmitsWindowsArray()
    {
        // Integer --app → FindWindowsByPid; two windows → composite multi-window capture.
        _fakeUia.WindowsByPidResult = [((nint)11, 4321, "Main"), ((nint)12, 4321, "Dialog")];
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"windows\":");
        StringAssert.Contains(TestAnsiConsole.Output, "\"captured\": true");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_MultiWindowByName_NonJson_Composites()
    {
        // Exact current-process-name match → real process enumeration hits FindWindowsByPid per process.
        var procName = Process.GetCurrentProcess().ProcessName;
        _fakeUia.WindowsByPidResult = [((nint)21, 100, "A"), ((nint)22, 100, "B")];
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", procName, "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "windows detected");
        StringAssert.Contains(TestAnsiConsole.Output, "Saved composite");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_MultiWindowAllCapturesFail_ReturnsError()
    {
        // Two windows discovered but every ScreenshotAsync throws → captures.Count == 0 → error.
        _fakeUia.WindowsByPidResult = [((nint)31, 4321, "Main"), ((nint)32, 4321, "Dialog")];
        _fakeUia.ScreenshotThrow = FakeGenericException;
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "-o", path]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "✗");
    }

    [TestMethod]
    public async Task Screenshot_SingleWindowWithOwnedDialog_Composites()
    {
        // No selector, no multi-window from discovery, but a cross-process owned dialog exists →
        // the single-session path detects it and composites session + owned window.
        _fakeWindowFinder.OwnedWindowsResult = [((nint)99, 4321, "Owned Dialog")];
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"windows\":");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_WindowZero_NonJson_CapturesSingle()
    {
        // --window 0 clears the missing-app guard (window is not null) but DiscoverAllWindows returns
        // null (0 is not > 0 and no app), so the single-window path runs.
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "0", "-o", path]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_WindowInvalidHandle_CapturesSingle()
    {
        // --window <invalid hwnd> → GetWindowThreadProcessId yields pid 0 → DiscoverAllWindows bails →
        // single-window path (exercises the direct-HWND discovery branch up to the pid==0 guard).
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "999999", "-o", path]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_ComException_ReturnsStaleError()
    {
        _fakeUia.ScreenshotThrow = FakeComException;

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Screenshot_GenericException_ReturnsGenericError()
    {
        // A non-COM exception from the single-window capture falls through to the catch-all, which maps
        // it to the generic-error envelope (mirrors the COMException stale-element path above).
        _fakeUia.ScreenshotThrow = FakeGenericException;

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Screenshot_WindowHandleValid_WithOwnedDialog_CompositesThroughSeam()
    {
        // Valid --window <hwnd>: ISystemUiQuery resolves a non-zero PID + title (what a live handle
        // yields on a real desktop) and a cross-process owned dialog exists → DiscoverAllWindows
        // returns two windows through the seam → multi-window composite. Exercises the direct-HWND
        // GetProcessIdForWindow / GetWindowText reads that were previously a native ceiling.
        _fakeSystemQuery.ProcessIdForWindowResult = 4321;
        _fakeSystemQuery.WindowTextResult = "Main Window";
        _fakeWindowFinder.OwnedWindowsResult = [((nint)0xD1A, 4321, "Owned Dialog")];
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "2748", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"windows\":");
        StringAssert.Contains(TestAnsiConsole.Output, "Main Window");   // title read through GetWindowText seam
        StringAssert.Contains(TestAnsiConsole.Output, "Owned Dialog");  // the second, owned window
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_WindowHandleValid_NoOwnedDialog_CapturesSingle()
    {
        // Valid --window <hwnd> but no owned windows → DiscoverAllWindows finds a single window and
        // returns null (Count is not > 1), so the single-window capture path runs. Still exercises the
        // seam's GetProcessIdForWindow / GetWindowText reads before the single-window fall-through.
        _fakeSystemQuery.ProcessIdForWindowResult = 4321;
        _fakeSystemQuery.WindowTextResult = "Solo Window";
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "2748", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(path));
    }
}

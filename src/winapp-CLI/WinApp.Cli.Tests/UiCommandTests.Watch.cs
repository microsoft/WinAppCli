// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    private static UiWatchEvent SampleEvent(string name = UiWatchEvents.Focus) => new()
    {
        Ts = "2026-01-01T00:00:00.0000000Z",
        Event = name,
        Element = new UiWatchElement { Name = "OK", ControlType = "button", Selector = "btn-ok-a1b2" },
    };

    [TestMethod]
    public async Task Watch_WithoutApp_ReturnsError()
    {
        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeWatcher.CallCount, "watcher should not run when the target app is missing");
    }

    [TestMethod]
    public async Task Watch_InvalidEvent_ReturnsError()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-e", "bogus-event", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeWatcher.CallCount);
    }

    [TestMethod]
    public async Task Watch_NoEventsSpecified_UsesDefaults()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(_fakeWatcher.LastRequest);
        CollectionAssert.AreEqual(
            UiWatchEvents.Default.ToArray(),
            _fakeWatcher.LastRequest!.Events.ToArray(),
            "watch should default to the canonical event set when none are supplied");
    }

    [TestMethod]
    public async Task Watch_SelectorProvided_IsResolvedAndAppliedToRequest()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0",
            Type = "Button",
            Name = "OK",
            AutomationId = "okButton",
            Selector = "btn-ok-a1b2",
        };

        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok-a1b2", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeWatcher.CallCount);
        Assert.AreEqual("btn-ok-a1b2", _fakeWatcher.LastRequest!.Selector);
        Assert.IsNotNull(_fakeWatcher.LastRequest.ScopeElement, "resolved selector element must flow to the watcher for subtree scoping");
        Assert.AreEqual("okButton", _fakeWatcher.LastRequest.ScopeElement!.AutomationId);
    }

    [TestMethod]
    public async Task Watch_SelectorNotFound_ReturnsElementNotFound()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        _fakeUia.FindSingleResult = null; // selector cannot be resolved

        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-missing-9999", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeWatcher.CallCount, "watcher must not run when the selector cannot be resolved");
    }

    [TestMethod]
    public async Task Watch_UnsupportedProperty_ReturnsError()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "-e", "property-changed", "-p", "IsEnabled", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeWatcher.CallCount);
    }

    [TestMethod]
    public async Task Watch_SupportedProperty_IsAcceptedAndNormalized()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "-e", "property-changed", "-p", "togglestate", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("ToggleState", _fakeWatcher.LastRequest!.Property,
            "supported --property should be normalized to canonical casing");
    }

    [TestMethod]
    public async Task Watch_MaxEvents_StopsAfterN()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        _fakeWatcher.ScriptedEvents.AddRange(
        [
            SampleEvent(), SampleEvent(UiWatchEvents.Invoke), SampleEvent(UiWatchEvents.Selection),
            SampleEvent(), SampleEvent(),
        ]);

        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--max-events", "2", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(2, _fakeWatcher.LastRequest!.MaxEvents);
        // Summary reports exactly the capped count.
        StringAssert.Contains(TestAnsiConsole.Output, "\"events\":2");
    }

    [TestMethod]
    public async Task Watch_Json_EmitsSummaryLine()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        _fakeWatcher.DurationMs = 123;
        _fakeWatcher.ScriptedEvents.Add(SampleEvent());

        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        // One event line (NDJSON) plus a summary line.
        StringAssert.Contains(output, "\"event\":\"focus\"");
        StringAssert.Contains(output, "\"events\":1");
        StringAssert.Contains(output, "\"durationMs\":123");
    }

    [TestMethod]
    public async Task Watch_OutputFile_ReceivesLines()
    {
        _fakeSession.SessionResult.WindowHandle = 0x1234;
        _fakeWatcher.ScriptedEvents.Add(SampleEvent());
        var outPath = Path.Combine(_tempDirectory.FullName, "watch.ndjson");

        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--json", "-o", outPath]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(outPath), "output file should be created");
        var contents = await File.ReadAllTextAsync(outPath, TestContext.CancellationToken);
        StringAssert.Contains(contents, "\"event\":\"focus\"");
        StringAssert.Contains(contents, "\"events\":1"); // summary written to the file too
    }

    [TestMethod]
    public async Task Watch_ElementScopedEvents_NoWindowHandle_FailsFast()
    {
        // Default session has WindowHandle == 0. Default events include element-scoped ones (focus),
        // so there is no safe UIA scope — the command must fail fast instead of watching the desktop.
        _fakeSession.SessionResult.WindowHandle = 0;
        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeWatcher.CallCount, "no safe scope — watcher must not run");
    }

    [TestMethod]
    public async Task Watch_WindowLifecycleOnly_NoWindowHandle_IsAllowed()
    {
        // Window open/close are process-scoped (WinEvent hook), so they don't need a window handle.
        _fakeSession.SessionResult.WindowHandle = 0;
        var command = GetRequiredService<UiWatchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "-e", "window-open", "-e", "window-close", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeWatcher.CallCount);
    }
}

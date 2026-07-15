// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <c>winapp ui wait-for</c>: missing-app / missing-selector, the --gone branch (JSON and
/// non-JSON), --value matching via an explicit --property and via the smart get-text fallback
/// (with --contains), the JSON timeout envelope, cancellation, and the COMException / generic
/// error branches. All fast and deterministic — tiny timeouts or first-poll resolution.
/// </summary>
public partial class UiCommandTests
{
    [TestMethod]
    public async Task WaitFor_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Gone_NonJson_Succeeds()
    {
        _fakeUia.FindSingleResult = null; // absent → disappeared on first poll
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--gone", "--timeout", "1000"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Gone_Json_Succeeds()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--gone", "--json", "--timeout", "1000"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"found\": false");
    }

    [TestMethod]
    public async Task WaitFor_Value_WithProperty_NonJson_Matches()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Edit", Name = "Field", Selector = "edit-1" };
        _fakeUia.PropertiesResult = new Dictionary<string, object?> { ["Name"] = "Ready" };

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["edit-1", "-a", "TestApp", "--value", "Ready", "--property", "Name", "--timeout", "1000"]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Value_SmartFallback_Contains_Json_Matches()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Document", Name = "Doc", Selector = "doc-1" };
        _fakeUia.GetTextResult = "hello world";

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["doc-1", "-a", "TestApp", "--value", "ELLO", "--contains", "--json", "--timeout", "1000"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"found\": true");
    }

    [TestMethod]
    public async Task WaitFor_Timeout_Json_ReturnsError()
    {
        _fakeUia.FindSingleResult = null; // never appears
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--json", "--timeout", "150"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"timedOut\": true");
    }

    [TestMethod]
    public async Task WaitFor_Cancelled_ReturnsError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled → ThrowIfCancellationRequested trips on the first poll
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--timeout", "1000"], cts.Token);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Com_ReturnsError()
    {
        // FindSingle exceptions are swallowed by the inner catch, so drive the COM path via GetText
        // (the smart-fallback value read), which is inside the outer try.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Document", Name = "Doc" };
        _fakeUia.GetTextThrow = FakeComException;

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--value", "x", "--timeout", "1000"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Generic_ReturnsError()
    {
        _fakeSession.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--timeout", "1000"]);
        Assert.AreEqual(1, exitCode);
    }
}

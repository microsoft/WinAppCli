// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    // ---------------------------------------------------------------------
    // send-keys (#562) — synthetic keyboard input
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task SendKeys_Json_EmitsEnvelope()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["ctrl+a", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("ctrl+a", result.GetProperty("keys").GetString());
        Assert.AreEqual("post-message", result.GetProperty("via").GetString());
        Assert.AreEqual(1, result.GetProperty("actionCount").GetInt32());
    }

    [TestMethod]
    public async Task SendKeys_DefaultTransport_IsPostMessage()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(WinApp.Cli.Helpers.KeyTransport.PostMessage, _fakeKeyboard.SendCalls[0].Transport);
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_SetsTransport()
    {
        // send-input now requires a resolvable target window (M9); give the session one.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "send-input"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(WinApp.Cli.Helpers.KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    [TestMethod]
    public async Task SendKeys_SequenceAndText_ParsesMultipleActions()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        // "down down enter" -> 3 key chords; "hello" -> 1 text action
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["down down enter hello", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(4, actions.Count);
        Assert.IsInstanceOfType<WinApp.Cli.Helpers.TextInput>(actions[3]);
    }

    [TestMethod]
    public async Task SendKeys_WithTarget_FocusesElementAndUsesElementHwnd()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0", Type = "Edit", Selector = "txt-name-1234", WindowHandle = 4242
        };

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["hello", "-a", "TestApp", "--target", "txt-name-1234", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(4242, _fakeKeyboard.SendCalls[0].Hwnd);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("txt-name-1234", result.GetProperty("target").GetString());
        Assert.AreEqual(4242, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    public async Task SendKeys_TargetNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["hello", "-a", "TestApp", "--target", "missing-0000", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_MissingKeys_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SendKeys_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SendKeys_InvalidVia_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "bogus", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_InvalidKeyToken_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["vk=0xZZ", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaSendInput_IsRejected()
    {
        // send-input is OS-wide, so a system-reserved combo (win+l) acts on the shell, not just the
        // target — the command must refuse it and never reach the keyboard transport.
        _fakeSession.SessionResult.WindowHandle = 4242; // a resolvable target so we reach the system-combo guard (M9)
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaPostMessage_StillSends()
    {
        // post-message is window-scoped (posts straight to the target HWND's queue), so it isn't
        // OS-wide and is not blocked — an alt+f4 posted to the window just closes that window.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["alt+f4", "-a", "TestApp", "--via", "post-message", "--json"]);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public void SendKeys_VerbatimOption_DocumentedInDescription()
    {
        // The --verbatim flag must be discoverable via --help / cli-schema and explain it types literally.
        StringAssert.Contains(UiSendKeysCommand.VerbatimOption.Description, "literal");
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_TypesEntireArgumentAsLiteralText()
    {
        // Without --verbatim this presses Down, Down, Enter; with it, the words are typed verbatim as
        // a single literal text action.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["down down enter", "-a", "TestApp", "--verbatim", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<WinApp.Cli.Helpers.TextInput>(actions[0]);
        Assert.AreEqual("down down enter", ((WinApp.Cli.Helpers.TextInput)actions[0]).Text);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(1, result.GetProperty("actionCount").GetInt32());
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_PreservesExactWhitespace()
    {
        // The normal path collapses internal whitespace to a single space; --verbatim keeps it exact.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["a  b", "-a", "TestApp", "--verbatim"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("a  b", ((WinApp.Cli.Helpers.TextInput)actions[0]).Text);
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_SystemComboTextViaSendInput_IsSentAsText()
    {
        // With --verbatim, "win+l" is literal text (a TextInput), not a chord — the system-combo guard
        // only inspects key chords, so the text is typed rather than refused even via send-input.
        _fakeSession.SessionResult.WindowHandle = 4242; // a resolvable target for send-input (M9)
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--verbatim", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.IsInstanceOfType<WinApp.Cli.Helpers.TextInput>(_fakeKeyboard.SendCalls[0].Actions[0]);
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_MissingKeys_ReturnsError()
    {
        // --verbatim still requires a value; an empty keys argument is rejected before injection.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--verbatim", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_WhitespaceOnly_IsTypedLiterally()
    {
        // --verbatim promises exact whitespace preservation, so a whitespace-only argument is legitimate
        // content to type (three spaces), not a "missing keys" error — unlike the normal path (M8).
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["   ", "-a", "TestApp", "--verbatim"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("   ", ((WinApp.Cli.Helpers.TextInput)actions[0]).Text);
    }

    [TestMethod]
    public async Task SendKeys_WhitespaceOnly_WithoutVerbatim_ReturnsError()
    {
        // Without --verbatim a whitespace-only argument has no tokens to interpret and stays an error (M8).
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["   ", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_NoResolvableTarget_ReturnsError()
    {
        // send-input is OS-wide; without a resolvable target window the command refuses rather than
        // injecting blindly into whatever has focus (M9). The default fake session has no window handle,
        // so the no-target guard fires before the foreground check ever runs.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "no keys should be sent when there is no resolvable target");
        Assert.AreEqual(0, _fakeForeground.Calls.Count, "the no-target guard fires before the foreground check");
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_ForegroundGuardDenies_AbortsWithoutSending()
    {
        // send-input verifies the foreground before injecting OS-wide; if the guard refuses (e.g. a locked
        // desktop or a wrong-window foreground), no keys are sent (M5).
        _fakeSession.SessionResult.WindowHandle = 4242; // resolvable target → reach the foreground gate
        _fakeForeground.Allow = false;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "no keys should be sent when the foreground guard refuses");
        Assert.AreEqual(1, _fakeForeground.Calls.Count);
        Assert.AreEqual("--via send-input", _fakeForeground.Calls[0].Action);
    }

    [TestMethod]
    public async Task SendKeys_ViaPostMessage_NoTarget_StillSends()
    {
        // post-message posts straight to the target HWND's message queue and is not OS-wide, so it does
        // not require the foreground gate or a resolvable window the way send-input does (M9 is scoped to
        // send-input). A zero handle still posts (the OS routes a 0 hwnd to the focused window).
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "post-message"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(0, _fakeForeground.Calls.Count, "post-message does not consult the foreground guard");
    }

    [TestMethod]
    public async Task SendKeys_ComException_ReturnsStaleErrorWithoutSending()
    {
        // A COMException surfacing from session/element resolution is a stale-element signal: the command
        // maps it to the stale error envelope and never reaches the keyboard transport.
        _fakeSession.ResolveThrow = FakeComException;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_GenericException_ReturnsErrorWithoutSending()
    {
        // Any non-COM failure inside the send pipeline is reported via the generic error envelope and
        // aborts before injecting keys.
        _fakeSession.ResolveThrow = FakeGenericException;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

}

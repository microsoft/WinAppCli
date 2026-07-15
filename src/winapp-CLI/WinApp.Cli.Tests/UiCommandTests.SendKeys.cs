// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Testing;
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
    public async Task SendKeys_GenericException_ReturnsGenericErrorWithoutSending()
    {
        // A non-COM exception during resolution falls through to the catch-all → generic-error envelope,
        // and no keys are sent (mirrors the COMException stale-element path above).
        _fakeSession.ResolveThrow = FakeGenericException;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    [DoNotParallelize] // temporarily swaps the process-wide ambient AnsiConsole to capture logger warnings
    public async Task SendKeys_PostMessageLiteralText_XamlWindow_WarnsTextMayNotBeDelivered()
    {
        // Literal text via post-message to a XAML (WinUI 3) window: the class name — read through
        // ISystemUiQuery — classifies as XAML, so the command warns that a posted WM_CHAR may be dropped
        // by the XAML input pipeline. The warning is advisory: the keys are still posted. Non-error
        // logger output routes through the static ambient AnsiConsole (TextWriterLogger), so we swap it
        // to a capturing console for the invoke; [DoNotParallelize] keeps that global swap isolated.
        _fakeSession.SessionResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "WinUIDesktopWin32WindowClass";

        var command = GetRequiredService<UiSendKeysCommand>();
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, ["hello", "-a", "TestApp", "--via", "post-message"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "the warning is advisory — the literal text is still posted");
        StringAssert.Contains(ambient.Output, "WM_CHAR", "a XAML target must trigger the dropped-WM_CHAR warning");
    }

    [TestMethod]
    [DoNotParallelize] // temporarily swaps the process-wide ambient AnsiConsole to capture logger warnings
    public async Task SendKeys_PostMessageLiteralText_NonXamlWindow_DoesNotWarn()
    {
        // Same literal-text-via-post-message shape, but a Win32 window (non-XAML class) consumes WM_CHAR,
        // so the XAML warning must NOT fire — it is scoped to XAML to avoid false-alarming other stacks.
        // We capture the ambient console the same way so the negative assertion is real, not vacuous.
        _fakeSession.SessionResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "Notepad";

        var command = GetRequiredService<UiSendKeysCommand>();
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, ["hello", "-a", "TestApp", "--via", "post-message"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.IsFalse(ambient.Output.Contains("WM_CHAR", StringComparison.Ordinal),
            "a non-XAML window class must not trigger the XAML dropped-WM_CHAR warning");
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaSendInput_WithAllowSystemKeys_Sends()
    {
        // --allow-system-keys opts in to OS/shell-wide combos (e.g. driving a global hotkey such as
        // PowerToys' win+shift+v): the guard is bypassed and the combo reaches the keyboard transport.
        _fakeSession.SessionResult.WindowHandle = 4242; // resolvable target + default foreground allow → reach the guard
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+shift+v", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(WinApp.Cli.Helpers.KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    [TestMethod]
    public async Task SendKeys_NonSystemCombo_ViaSendInput_WithAllowSystemKeys_Unaffected()
    {
        // The flag only relaxes system-reserved combos; an ordinary combo behaves identically with or
        // without it (no accidental change to the normal path).
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--via", "send-input", "--allow-system-keys"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaSendInput_WithoutAllow_StaysRejected()
    {
        // Default (no flag) still refuses system combos on send-input — the opt-in must not weaken the
        // default safety posture.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public void SendKeys_AllowSystemKeysOption_DocumentedInDescription()
    {
        // Discoverable via --help / cli-schema and explains it applies to send-input.
        StringAssert.Contains(UiSendKeysCommand.AllowSystemKeysOption.Description, "send-input");
        StringAssert.Contains(UiSendKeysCommand.AllowSystemKeysOption.Description, "system");
    }

    // COR-01 — SEC-01: win+l must be refused even when --allow-system-keys is set
    [TestMethod]
    public async Task SendKeys_WinL_ViaSendInput_WithAllowSystemKeys_IsStillRefused()
    {
        // win+l triggers LockWorkStation() via the shell hook and is unrecoverable from automation.
        // It must be blocked EVEN when --allow-system-keys is passed — the never-bypassable guard
        // must fire before the soft-combo/allow path and prevent injection.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "win+l must never reach the keyboard transport");
    }

    // COR-01 — SEC-01: a benign win+<key> combo IS allowed with --allow-system-keys
    [TestMethod]
    public async Task SendKeys_WinR_ViaSendInput_WithAllowSystemKeys_IsAllowed()
    {
        // win+r (Run dialog) is a soft-blocked system combo that the caller can opt into with
        // --allow-system-keys. It must pass the never-bypassable guard (only win+l is hard-blocked)
        // and reach the keyboard transport.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+r", "-a", "TestApp", "--via", "send-input", "--allow-system-keys"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(WinApp.Cli.Helpers.KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    // COR-01 — SEC-02: --allow-system-keys with post-message is a no-op (exit 0, warning emitted)
    [TestMethod]
    public async Task SendKeys_AllowSystemKeys_WithPostMessage_IsNoOpAndWarns()
    {
        // post-message is already window-scoped and never blocks system combos, so --allow-system-keys
        // has no effect with it. The command must succeed (exit 0) and still deliver the keystrokes;
        // a warning is logged but the exit code stays 0.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--via", "post-message", "--allow-system-keys"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "keys should still be sent via post-message");
        Assert.AreEqual(WinApp.Cli.Helpers.KeyTransport.PostMessage, _fakeKeyboard.SendCalls[0].Transport);
    }

    // M1: --allow-system-keys + --json + post-message → no-op warning visible in JSON warnings array
    [TestMethod]
    public async Task SendKeys_AllowSystemKeys_PostMessage_Json_WarningInResult()
    {
        // --json consumers see the no-op warning even though the global logger is suppressed in JSON mode.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--via", "post-message", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        var warnings = result.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength(), "one no-op warning expected in JSON");
        StringAssert.Contains(warnings[0].GetString(), "--allow-system-keys");
        StringAssert.Contains(warnings[0].GetString(), "post-message");
    }

    // M1: --allow-system-keys + --json + send-input + system combo → audit warning visible in JSON
    [TestMethod]
    public async Task SendKeys_AllowSystemKeys_SendInput_Json_AuditWarningInResult()
    {
        // --json consumers see the injection audit trail even though the global logger is suppressed.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+shift+v", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        var warnings = result.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength(), "one audit warning expected in JSON");
        StringAssert.Contains(warnings[0].GetString(), "--allow-system-keys");
        StringAssert.Contains(warnings[0].GetString(), "win+<key>");
    }

    // M1: no --allow-system-keys flag → warnings array is empty in JSON
    [TestMethod]
    public async Task SendKeys_NoAllowFlag_Json_WarningsIsEmpty()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0, result.GetProperty("warnings").GetArrayLength(), "no warnings when flag is absent");
    }

    // LOW: cmd alias for win — cmd+l stays hard-blocked even with --allow-system-keys
    [TestMethod]
    public async Task SendKeys_CmdL_ViaSendInput_WithAllowSystemKeys_IsStillRefused()
    {
        // cmd is an alias for win in the key grammar; cmd+l must be blocked unconditionally.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["cmd+l", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "cmd+l must never reach the keyboard transport");
    }

    // LOW: win+shift+l (extra modifier alongside never-bypassable) stays hard-blocked
    [TestMethod]
    public async Task SendKeys_WinShiftL_ViaSendInput_WithAllowSystemKeys_IsStillRefused()
    {
        // Extra modifiers do not defeat the win+l hard block — the guard sees win modifier + VkL.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+shift+l", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "win+shift+l must never reach the keyboard transport");
    }

    // LOW: lone right-Win key (vk=0x5c) is soft-blocked without --allow-system-keys
    [TestMethod]
    public async Task SendKeys_LoneRWin_ViaSendInput_WithoutAllow_IsBlocked()
    {
        // The right-Win key (VkRWin = 0x5c) opens Start — refused without --allow-system-keys.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["vk=0x5c", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "lone right-Win key must be blocked without --allow-system-keys");
    }

    // LOW: lone right-Win key (vk=0x5c) IS allowed with --allow-system-keys
    [TestMethod]
    public async Task SendKeys_LoneRWin_ViaSendInput_WithAllow_Sends()
    {
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["vk=0x5c", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "right-Win key should be sent when opted in");
        Assert.AreEqual(WinApp.Cli.Helpers.KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    // LOW: system combo refused without flag → error text contains --allow-system-keys
    [TestMethod]
    public async Task SendKeys_SystemCombo_Refused_ErrorMentionsAllowFlag()
    {
        // The logged error message must guide the caller to --allow-system-keys so the fix is actionable.
        _fakeSession.SessionResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+r", "-a", "TestApp", "--via", "send-input"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--allow-system-keys");
    }

}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiSendKeysCommand : Command, IShortDescription
{
    public string ShortDescription => "Send synthetic keyboard input (keys, combos, and text) to a window";

    public static Argument<string?> KeysArgument { get; } = new("keys")
    {
        Description = "Keys to send. Whitespace-separated tokens: named keys (down, enter, tab, esc, f5), " +
                      "modifier combos (ctrl+shift+t, alt+f4), raw virtual keys (vk=0x42), or literal text (hello). " +
                      "Use text=<literal> to type a single value verbatim when it would otherwise be read as a key " +
                      "name or combo (text=enter types \"enter\"; text=ctrl+a types \"ctrl+a\"); backslash escapes \\s \\t " +
                      "\\n \\r \\\\ are supported (text=a\\s\\sb types \"a  b\"). To type the whole argument literally " +
                      "without escaping each token, pass --verbatim instead. " +
                      "Quote multi-token strings, e.g. \"ctrl+a delete\".",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Option<string?> TargetOption { get; } = new("--target")
    {
        Description = "Optional selector (slug or text) to focus before sending keys."
    };

    public static Option<string> ViaOption { get; } = new("--via")
    {
        Description = "Transport: post-message (default, HWND-targeted, bypasses UIPI; typed text raises TextChanged " +
                      "but not a per-character KeyDown) or send-input (OS-wide; typed text raises a real per-character " +
                      "KeyDown + TextChanged). Named keys and combos raise KeyDown on both, but keyboard " +
                      "accelerators/shortcuts (KeyboardAccelerator, e.g. ctrl+t) only fire via send-input.",
        DefaultValueFactory = _ => "post-message"
    };

    public static Option<bool> VerbatimOption { get; } = new("--verbatim")
    {
        Description = "Type the entire keys argument as literal text — no named-key, combo, or vk= interpretation, " +
                      "and exact whitespace preserved. The whole-argument form of the per-token text= escape: " +
                      "--verbatim \"down down enter\" types the words instead of pressing Down, Down, Enter."
    };

    public static Option<bool> AllowSystemKeysOption { get; } = new("--allow-system-keys")
    {
        Description = "Allow synthesizing system-/shell-reserved combos (win+<key>, alt+f4, alt+tab, ctrl+esc, …) via " +
                      "--via send-input, which are refused by default because they act on the OS/shell beyond the " +
                      "target app. Opt in to drive global hotkeys (e.g. PowerToys' win+shift+v, win+r). " +
                      "No effect on --via post-message (already window-scoped; a warning is emitted if set without send-input). " +
                      "Note: win+l stays blocked even with this flag — it locks the workstation (LockWorkStation() via " +
                      "the shell hook), which is unrecoverable from automation. Windows still blocks secure sequences " +
                      "such as ctrl+alt+del (SAS) from injected input regardless of this flag."
    };

    public UiSendKeysCommand()
        : base("send-keys", "Send synthetic keyboard input to a window. Supports named keys (down, enter, tab), " +
               "modifier combos (ctrl+shift+t), raw virtual keys (vk=0xNN), and literal text. " +
               "Use --verbatim to type the whole argument literally, or --target to focus an element first. " +
               "Two transports via --via: post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide). " +
               "For per-keystroke KeyDown on typed text (e.g. a WinUI 3/WPF TextBox), use --via send-input.")
    {
        Arguments.Add(KeysArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(TargetOption);
        Options.Add(ViaOption);
        Options.Add(VerbatimOption);
        Options.Add(AllowSystemKeysOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IKeyboardInput keyboardInput,
        IForegroundGuard foregroundGuard,
        IAnsiConsole ansiConsole,
        ILogger<UiSendKeysCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var keysStr = parseResult.GetValue(KeysArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var target = parseResult.GetValue(TargetOption);
            var viaStr = parseResult.GetValue(ViaOption) ?? "post-message";
            var verbatim = parseResult.GetValue(VerbatimOption);
            var allowSystemKeys = parseResult.GetValue(AllowSystemKeysOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            // For --verbatim, whitespace is legitimate content to type (the help promises exact
            // whitespace preservation), so only reject a genuinely empty argument. Without --verbatim,
            // a whitespace-only argument has no tokens to interpret, so it stays an error. (The IsNullOrEmpty
            // operand is also what lets the compiler treat keysStr as non-null on the fall-through path.)
            if (string.IsNullOrEmpty(keysStr) || (!verbatim && string.IsNullOrWhiteSpace(keysStr)))
            {
                logger.LogError("{Symbol} Keys are required. Usage: winapp ui send-keys <keys> -a <app>", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "Keys are required. Usage: winapp ui send-keys <keys> -a <app>");
                return 1;
            }

            if (!TryParseTransport(viaStr, out var transport))
            {
                logger.LogError("{Symbol} Invalid --via value '{Via}'. Use post-message or send-input.", UiSymbols.Error, viaStr);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"Invalid --via value '{viaStr}'. Use post-message or send-input.");
                return 1;
            }

            // SEC-02: --allow-system-keys only applies to send-input; with post-message the transport is
            // already window-scoped so system combos are never blocked and the flag has no effect.
            var warnings = new List<string>();
            if (allowSystemKeys && transport != KeyTransport.SendInput)
            {
                logger.LogWarning(
                    "{Symbol} --allow-system-keys only applies to --via send-input and has no effect with " +
                    "--via post-message (post-message is already window-scoped and never blocks system combos).",
                    UiSymbols.Warning);
                warnings.Add(
                    "--allow-system-keys only applies to --via send-input and has no effect with " +
                    "--via post-message (post-message is already window-scoped and never blocks system combos).");
            }

            IReadOnlyList<KeyAction> actions;
            try
            {
                // --verbatim types the whole argument literally (no key/combo/vk=/text= parsing, no
                // whitespace collapsing); otherwise interpret the friendly key grammar token by token.
                // keysStr is non-null here: the guard above rejected null/empty.
                actions = verbatim ? KeyStringParser.ParseVerbatim(keysStr) : KeyStringParser.Parse(keysStr);
            }
            catch (FormatException ex)
            {
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, ex.Message);
                return 1;
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var targetHwnd = session.WindowHandle;

                if (!string.IsNullOrWhiteSpace(target))
                {
                    var selector = selectorService.Parse(target);
                    var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);

                    if (element is null)
                    {
                        UiErrors.ElementNotFound(logger, target, json);
                        return 1;
                    }

                    await uiAutomation.FocusAsync(session, element, cancellationToken);
                    targetHwnd = element.WindowHandle ?? session.WindowHandle;
                }

                // Bring the target window to the foreground so input is routed to it.
                if (targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken);
                }

                // send-input is OS-wide: it lands on whatever window is actually in the foreground. If
                // SetForegroundWindow didn't take (focus-stealing prevention, a UAC prompt, another app
                // grabbing focus, or a locked/secure desktop), injecting now would type into the wrong
                // window. Verify the foreground belongs to the target before sending. (post-message posts
                // straight to the target HWND's queue, so it isn't affected.)
                if (transport == KeyTransport.SendInput)
                {
                    // Unlike a coordinate gesture (which targets a screen point), keystrokes have no
                    // location — without a resolvable target window there is nothing to verify the
                    // foreground against, so OS-wide injection would type blindly into whatever has
                    // focus. Refuse rather than send to an unknown window.
                    if (targetHwnd == 0)
                    {
                        logger.LogError(
                            "{Symbol} --via send-input needs a resolvable target window, but none was found. Pass --window <hwnd>, ensure -a/--app resolves a window, or use --target to focus an element first.",
                            UiSymbols.Error);
                        UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget,
                            "send-input needs a resolvable target window, but none was found — refusing OS-wide keyboard injection without a known target. Pass --window/--app or --target.");
                        return 1;
                    }

                    if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "--via send-input"))
                    {
                        return 1;
                    }
                }

                // WM_CHAR posted to a WinUI 3 / XAML host window is not turned into text by the XAML input
                // pipeline, so typed literal text silently no-ops there. Warn — but only when the target
                // actually looks like a XAML window — rather than false-alarming on Win32/WPF/Electron
                // apps that do consume WM_CHAR. (Named keys/combos still post KeyDown regardless.)
                if (ShouldWarnPostMessageTextDropped(
                        transport == KeyTransport.PostMessage,
                        actions.Any(a => a is TextInput),
                        FrameworkHint.IsLikelyXaml(targetHwnd)))
                {
                    logger.LogWarning(
                        "{Symbol} Literal text via --via post-message may not be delivered to WinUI 3 / XAML apps (WM_CHAR is dropped by the input pipeline). Use --via send-input if the text does not appear.",
                        UiSymbols.Warning);
                }

                // send-input is OS-wide, so a system-reserved combo (win+l, alt+f4, ctrl+shift+esc, …)
                // would act on the OS/shell rather than just the target app (lock the session, close the
                // window, open Task Manager). Refuse to synthesize them via send-input — the blast radius
                // beyond the target window makes silently sending them too dangerous for an automation run.
                if (transport == KeyTransport.SendInput)
                {
                    // win+l (LockWorkStation) is unconditionally blocked even with --allow-system-keys:
                    // injecting it OS-wide locks the interactive session with no recovery path from
                    // automation (breaks CI and remote-desktop sessions irreversibly). Return early so
                    // it does not fall through into the soft-combo / allow path below.
                    var neverBypassable = SystemKeyGuard.FindNeverBypassableCombos(actions);
                    if (neverBypassable.Count > 0)
                    {
                        logger.LogError(
                            "{Symbol} Refusing to synthesize {Combos} via --via send-input — this stays blocked " +
                            "even with --allow-system-keys because it locks the workstation (unrecoverable from automation). " +
                            "--allow-system-keys is for app-registered global hotkeys (e.g. win+r, win+shift+v), not session-locking combos.",
                            UiSymbols.Error, string.Join(", ", neverBypassable));
                        UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                            $"Refusing to synthesize {string.Join(", ", neverBypassable)} via --via send-input. " +
                            "This combo locks the workstation (unrecoverable from automation) and stays blocked even with " +
                            "--allow-system-keys. Use --allow-system-keys only for app-registered global hotkeys (e.g. win+r, win+shift+v).");
                        return 1;
                    }

                    var systemCombos = SystemKeyGuard.FindSystemCombos(actions);
                    if (systemCombos.Count > 0)
                    {
                        if (!allowSystemKeys)
                        {
                            logger.LogError(
                                "{Symbol} Refusing to synthesize system-reserved key(s) via --via send-input: {Combos}. " +
                                "These act on the OS/shell (e.g. win+l locks the session, alt+f4 closes the window, ctrl+alt+del is intercepted by Windows), not just the target app. " +
                                "Pass --allow-system-keys to opt in (e.g. to drive a global hotkey).",
                                UiSymbols.Error, string.Join(", ", systemCombos));
                            UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                                $"Refusing to synthesize system-reserved key(s) via --via send-input: {string.Join(", ", systemCombos)}. " +
                                "These act on the OS/shell rather than just the target app. Pass --allow-system-keys to opt in.");
                            return 1;
                        }

                        // Caller explicitly opted in with --allow-system-keys (e.g. to fire a global hotkey such as
                        // PowerToys' win+shift+v). Record the bypass so it's auditable in persisted logs, then fall
                        // through and inject. (Windows still blocks secure sequences like ctrl+alt+del regardless.)
                        var systemCombosStr = string.Join(", ", systemCombos);
                        logger.LogWarning(
                            "{Symbol} Injecting system-reserved key(s) via --via send-input because --allow-system-keys was set: {Combos}. " +
                            "These act on the OS/shell beyond the target app.",
                            UiSymbols.Warning, systemCombosStr);
                        warnings.Add(
                            $"Injecting system-reserved key(s) via --via send-input because --allow-system-keys was set: {systemCombosStr}. " +
                            "These act on the OS/shell beyond the target app.");
                    }
                }

                keyboardInput.Send(targetHwnd, actions, transport);

                if (json)
                {
                    var result = new UiSendKeysResult
                    {
                        Keys = keysStr,
                        Via = transport == KeyTransport.PostMessage ? "post-message" : "send-input",
                        ActionCount = actions.Count,
                        Target = target,
                        Hwnd = targetHwnd,
                        Warnings = warnings
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiSendKeysResult));
                }
                else
                {
                    // Don't echo the raw keystrokes to the human log — in CI those logs are persisted and
                    // shared, and key sequences may carry passwords / tokens being typed into a field. The
                    // structured --json result still includes `keys` for callers that opt in (and it's
                    // already on the command line); the plain log reports only the action count.
                    logger.LogInformation("{Symbol} Sent {ActionCount} key action(s) via {Via}",
                        UiSymbols.Check, actions.Count, transport == KeyTransport.PostMessage ? "post-message" : "send-input");
                }

                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        /// <summary>
        /// Whether to warn that literal typed text may be silently dropped: only when posting WM_CHAR
        /// (<paramref name="isPostMessage"/>) AND the payload actually contains literal text AND the
        /// target looks like a XAML window (WinUI 3 / UWP), which drops posted WM_CHAR text. Pure so
        /// the gate is unit-testable without a live XAML window; the command computes the three inputs
        /// (the third via <see cref="FrameworkHint.IsLikelyXaml"/>) and routes the warning through here.
        /// </summary>
        internal static bool ShouldWarnPostMessageTextDropped(bool isPostMessage, bool hasLiteralText, bool targetLooksXaml)
            => isPostMessage && hasLiteralText && targetLooksXaml;

        private static bool TryParseTransport(string via, out KeyTransport transport)
        {
            switch (via.ToLowerInvariant().Replace("_", "-"))
            {
                case "post-message":
                case "postmessage":
                    transport = KeyTransport.PostMessage;
                    return true;
                case "send-input":
                case "sendinput":
                    transport = KeyTransport.SendInput;
                    return true;
                default:
                    transport = KeyTransport.PostMessage;
                    return false;
            }
        }
    }
}

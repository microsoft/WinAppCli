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

    public UiSendKeysCommand()
        : base("send-keys", "Send synthetic keyboard input to a window. Supports named keys (down, enter, tab), " +
               "modifier combos (ctrl+shift+t), raw virtual keys (vk=0xNN), and literal text. " +
               "Use --target to focus an element first. " +
               "Two transports via --via: post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide). " +
               "For per-keystroke KeyDown on typed text (e.g. a WinUI 3/WPF TextBox), use --via send-input.")
    {
        Arguments.Add(KeysArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(TargetOption);
        Options.Add(ViaOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IKeyboardInput keyboardInput,
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

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(keysStr))
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

            IReadOnlyList<KeyAction> actions;
            try
            {
                actions = KeyStringParser.Parse(keysStr);
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
                if (transport == KeyTransport.SendInput &&
                    !ForegroundGuard.TryEnsureForeground(targetHwnd, logger, json, "--via send-input"))
                {
                    return 1;
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
                    var systemCombos = SystemKeyGuard.FindSystemCombos(actions);
                    if (systemCombos.Count > 0)
                    {
                        logger.LogError(
                            "{Symbol} Refusing to synthesize system-reserved key(s) via --via send-input: {Combos}. " +
                            "These act on the OS/shell (e.g. win+l locks the session, alt+f4 closes the window, ctrl+alt+del is intercepted by Windows), not just the target app.",
                            UiSymbols.Error, string.Join(", ", systemCombos));
                        UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                            $"Refusing to synthesize system-reserved key(s) via --via send-input: {string.Join(", ", systemCombos)}. " +
                            "These act on the OS/shell rather than just the target app.");
                        return 1;
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
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiSendKeysResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} Sent keys \"{Keys}\" via {Via}",
                        UiSymbols.Check, keysStr, transport == KeyTransport.PostMessage ? "post-message" : "send-input");
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

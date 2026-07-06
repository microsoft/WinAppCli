// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace WinApp.Cli.Commands;

/// <summary>
/// Shared options used across all winapp ui commands.
/// </summary>
internal static class SharedUiOptions
{
    public static Option<string?> AppOption { get; }
    public static Option<long?> WindowOption { get; }
    public static Argument<string?> SelectorArgument { get; }
    public static Option<int> DepthOption { get; }
    public static Option<int> MaxResultsOption { get; }
    public static Option<string?> OutputOption { get; }
    public static Option<int> TimeoutOption { get; }
    public static Option<string?> PropertyOption { get; }
    public static Argument<string?> ValueArgument { get; }
    public static Option<bool> CaptureScreenOption { get; }
    public static Option<bool> FocusOption { get; }
    public static Option<bool> InteractiveOption { get; }
    public static Option<bool> HideDisabledOption { get; }
    public static Option<bool> HideOffscreenOption { get; }
    public static Option<bool> AllowMinimizedOption { get; }

    static SharedUiOptions()
    {
        AppOption = new Option<string?>("--app", "-a")
        {
            Description = "Target app (process name, window title, or PID). Lists windows if ambiguous."
        };

        WindowOption = new Option<long?>("--window", "-w")
        {
            Description = "Target window by HWND (stable handle from list output). Takes precedence over --app."
        };

        SelectorArgument= new Argument<string?>("selector")
        {
            Description = "Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId",
            Arity = ArgumentArity.ZeroOrOne
        };

        DepthOption = new Option<int>("--depth", "-d")
        {
            Description = "Tree inspection depth",
            DefaultValueFactory = _ => 4
        };

        MaxResultsOption = new Option<int>("--max")
        {
            Description = "Maximum search results",
            DefaultValueFactory = _ => 50
        };

        OutputOption = new Option<string?>("--output", "-o")
        {
            Description = "Save output to file path (e.g., screenshot)"
        };

        TimeoutOption = new Option<int>("--timeout", "-t")
        {
            Description = "Timeout in milliseconds",
            DefaultValueFactory = _ => 5000
        };

        PropertyOption = new Option<string?>("--property", "-p")
        {
            Description = "Property name to read or filter on"
        };

        ValueArgument = new Argument<string?>("value")
        {
            Description = "Value to set (text for TextBox/ComboBox, number for Slider)",
            Arity = ArgumentArity.ZeroOrOne
        };

        CaptureScreenOption = new Option<bool>("--capture-screen")
        {
            Description = "Capture from screen DC via BitBlt (includes popups/overlays not owned by the target). Implies --focus."
        };

        FocusOption = new Option<bool>("--focus")
        {
            Description = "Bring the target window to the foreground before capture. Already implied by --capture-screen."
        };

        InteractiveOption = new Option<bool>("--interactive", "-i")
        {
            Description = "Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8."
        };

        HideDisabledOption = new Option<bool>("--hide-disabled")
        {
            Description = "Hide disabled elements from output"
        };

        HideOffscreenOption = new Option<bool>("--hide-offscreen")
        {
            Description = "Hide offscreen elements from output"
        };

        AllowMinimizedOption = new Option<bool>("--allow-minimized")
        {
            Description = "Target the app as-is even when minimized. By default a minimized window is " +
                          "restored first so its full UI tree is realized (minimized apps expose a sparser tree).",
            Recursive = true
        };
    }

    /// <summary>
    /// Whether <see cref="IUiSessionService"/> should restore the target window if it is minimized.
    /// Honors the global <c>--allow-minimized</c> opt-out.
    /// </summary>
    public static bool ShouldRestoreMinimized(ParseResult parseResult)
        => !parseResult.GetValue(AllowMinimizedOption);
}

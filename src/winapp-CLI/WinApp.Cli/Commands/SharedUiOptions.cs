// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

/// <summary>
/// Shared options used across all winapp ui commands.
/// </summary>
internal static class SharedUiOptions
{
    public static Option<string?> AppOption { get; }
    public static Option<long?> WindowOption { get; }
    public static Option<string?> ModeOption { get; }
    public static Argument<string?> SelectorArgument { get; }
    public static Option<int> DepthOption { get; }
    public static Option<int> MaxResultsOption { get; }
    public static Option<string?> OutputOption { get; }
    public static Option<int> TimeoutOption { get; }
    public static Option<string?> PropertyOption { get; }
    public static Option<string?> TextOption { get; }

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

        ModeOption = new Option<string?>("--mode")
        {
            Description = "Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default)"
        };

        SelectorArgument = new Argument<string?>("selector")
        {
            Description = "Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name",
            Arity = ArgumentArity.ZeroOrOne
        };

        DepthOption = new Option<int>("--depth", "-d")
        {
            Description = "Tree inspection depth",
            DefaultValueFactory = _ => 3
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

        TimeoutOption = new Option<int>("--timeout", "-w")
        {
            Description = "Timeout in milliseconds",
            DefaultValueFactory = _ => 5000
        };

        PropertyOption = new Option<string?>("--property", "-p")
        {
            Description = "Property name to read or filter on"
        };

        TextOption = new Option<string?>("--text")
        {
            Description = "Text value to set or type"
        };
    }
}

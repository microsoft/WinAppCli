// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Source-generated JSON serializer context for UI automation models (NativeAOT-safe).
/// </summary>
[JsonSerializable(typeof(UiElement))]
[JsonSerializable(typeof(UiElement[]))]
[JsonSerializable(typeof(UiSessionInfo))]
[JsonSerializable(typeof(UiStatusResult))]
[JsonSerializable(typeof(UiInspectResult))]
[JsonSerializable(typeof(UiSearchResult))]
[JsonSerializable(typeof(UiPropertyResult))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(UiInvokeResult))]
[JsonSerializable(typeof(UiClickResult))]
[JsonSerializable(typeof(UiScreenshotResult))]
[JsonSerializable(typeof(UiScreenshotResult[]))]
[JsonSerializable(typeof(UiGetValueResult))]
[JsonSerializable(typeof(UiWaitForResult))]
[JsonSerializable(typeof(UiScrollResult))]
[JsonSerializable(typeof(UiSetValueResult))]
[JsonSerializable(typeof(UiFocusResult))]
[JsonSerializable(typeof(UiScrollIntoViewResult))]
[JsonSerializable(typeof(WindowInfo))]
[JsonSerializable(typeof(WindowInfo[]))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UiJsonContext : JsonSerializerContext;

// JSON output models for --json mode

internal sealed class UiStatusResult
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? WindowTitle { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiInspectResult
{
    public UiElement[] Elements { get; set; } = [];
}

internal sealed class UiSearchResult
{
    public int MatchCount { get; set; }
    public bool HasMore { get; set; }
    public UiElement[] Matches { get; set; } = [];
}

internal sealed class UiPropertyResult
{
    public string ElementId { get; set; } = "";
    public Dictionary<string, string?> Properties { get; set; } = [];
}

internal sealed class UiInvokeResult
{
    public string ElementId { get; set; } = "";
    public string Pattern { get; set; } = "";
    public long Hwnd { get; set; }
}

internal sealed class UiClickResult
{
    public string ElementId { get; set; } = "";
    public string ClickType { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiScreenshotResult
{
    public string? ElementId { get; set; }
    public string FilePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int ProcessId { get; set; }
    public string? WindowTitle { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiWaitForResult
{
    public bool Found { get; set; }
    public int WaitedMs { get; set; }
    public UiElement? Element { get; set; }
}

internal sealed class WindowInfo
{
    public long Hwnd { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? Title { get; set; }
    public string? Label { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long OwnerHwnd { get; set; }
    public string? ClassName { get; set; }
    public bool IsForeground { get; set; }
}

internal sealed class UiGetValueResult
{
    public string ElementId { get; set; } = "";
    public string? Text { get; set; }
}

internal sealed class UiScrollResult
{
    public string ElementId { get; set; } = "";
    public string? Direction { get; set; }
    public string? To { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiSetValueResult
{
    public string ElementId { get; set; } = "";
    public long Hwnd { get; set; }
}

internal sealed class UiFocusResult
{
    public string ElementId { get; set; } = "";
    public long Hwnd { get; set; }
}

internal sealed class UiScrollIntoViewResult
{
    public string ElementId { get; set; } = "";
    public long Hwnd { get; set; }
}

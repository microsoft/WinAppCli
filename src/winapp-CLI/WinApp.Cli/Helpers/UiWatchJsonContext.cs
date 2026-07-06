// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Compact (non-indented) source-generated JSON context for <c>winapp ui watch</c> NDJSON output.
/// The main <see cref="UiJsonContext"/> writes indented JSON, which is unsuitable for the
/// one-object-per-line streaming format the watch command emits; this context forces
/// <c>WriteIndented = false</c> so each event serializes to a single line.
/// </summary>
[JsonSerializable(typeof(UiWatchEvent))]
[JsonSerializable(typeof(UiWatchSummary))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UiWatchJsonContext : JsonSerializerContext;

/// <summary>A single UI event emitted by <c>winapp ui watch</c>.</summary>
internal sealed class UiWatchEvent
{
    /// <summary>ISO-8601 timestamp (round-trip, UTC) of when the event was observed.</summary>
    public string Ts { get; set; } = "";

    /// <summary>Canonical event name (focus, window-open, invoke, ...).</summary>
    public string Event { get; set; } = "";

    /// <summary>The element associated with the event, when it could be resolved.</summary>
    public UiWatchElement? Element { get; set; }

    /// <summary>Free-form detail (raw event id, changed property, structure-change kind, ...).</summary>
    public string? Detail { get; set; }
}

/// <summary>Minimal element descriptor carried on a <see cref="UiWatchEvent"/>.</summary>
internal sealed class UiWatchElement
{
    public string? Selector { get; set; }
    public string? Name { get; set; }
    public string? ControlType { get; set; }
}

/// <summary>Final summary line emitted by <c>winapp ui watch --json</c> after the listen loop ends.</summary>
internal sealed class UiWatchSummary
{
    public int Events { get; set; }
    public long DurationMs { get; set; }
}

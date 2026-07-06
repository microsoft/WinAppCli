// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Canonical <c>winapp ui watch</c> event names. Kept as string constants (not an enum) to match
/// the CLI convention where enum-like options are <c>string?</c> with the allowed values listed in
/// the option description.
/// </summary>
internal static class UiWatchEvents
{
    public const string Focus = "focus";
    public const string WindowOpen = "window-open";
    public const string WindowClose = "window-close";
    public const string Invoke = "invoke";
    public const string Selection = "selection";
    public const string TextChanged = "text-changed";
    public const string PropertyChanged = "property-changed";
    public const string StructureChanged = "structure-changed";
    public const string Notification = "notification";
    public const string LiveRegion = "live-region";

    /// <summary>All recognized event names, in display order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Focus, WindowOpen, WindowClose, Invoke, Selection,
        TextChanged, PropertyChanged, StructureChanged, Notification, LiveRegion,
    ];

    /// <summary>The sensible default set used when the caller passes no <c>-e/--event</c>.</summary>
    public static readonly IReadOnlyList<string> Default =
    [
        Focus, WindowOpen, WindowClose, Invoke, Selection,
    ];

    /// <summary>
    /// Window lifecycle events delivered via a process-scoped WinEvent hook. These do not require a
    /// UIA element scope (window handle / selector) — they are filtered to the target PID by the hook.
    /// </summary>
    public static readonly IReadOnlyList<string> WindowLifecycle = [WindowOpen, WindowClose];

    /// <summary>
    /// Property names that <c>property-changed</c> can subscribe to and filter on. UIA exposes many
    /// more, but the watcher only registers handlers for this set; anything else would silently yield
    /// zero events, so <c>ui watch</c> rejects unsupported <c>--property</c> values up front.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProperties = ["Name", "Value", "ToggleState"];

    /// <summary>
    /// True when <paramref name="events"/> contains any event that must be scoped to a UIA element
    /// subtree (i.e. anything other than the process-scoped window open/close lifecycle events). Such
    /// events require a target window handle (and optionally a selector) — without one there is no safe
    /// scope and the watch must fail fast rather than register on the all-process desktop root.
    /// </summary>
    public static bool RequiresElementScope(IEnumerable<string> events)
        => events.Any(e => !WindowLifecycle.Contains(e));
}

/// <summary>Resolved parameters for a single <c>winapp ui watch</c> run.</summary>
internal sealed class UiWatchRequest
{
    /// <summary>Event names to listen for (already validated + defaulted).</summary>
    public required IReadOnlyList<string> Events { get; init; }

    /// <summary>Optional property-name filter for <c>property-changed</c> events.</summary>
    public string? Property { get; init; }

    /// <summary>Optional selector text scoping events to an element subtree.</summary>
    public string? Selector { get; init; }

    /// <summary>
    /// The element the <see cref="Selector"/> resolved to (via <c>IUiAutomationService.FindSingleElementAsync</c>),
    /// or <see langword="null"/> when no selector was supplied. When set, the watcher re-resolves this
    /// element on its own UIA thread (by AutomationId / Name within the target window) and registers UIA
    /// event handlers scoped to that element's subtree instead of the whole window.
    /// </summary>
    public Models.UiElement? ScopeElement { get; init; }

    /// <summary>Stop after this many events. 0 = unlimited.</summary>
    public int MaxEvents { get; init; }

    /// <summary>Listen for this many seconds. 0 = until cancellation (Ctrl+C).</summary>
    public int DurationSec { get; init; }
}

/// <summary>Result of a completed watch loop.</summary>
internal readonly record struct UiWatchOutcome(int Events, long DurationMs);

/// <summary>
/// Listens for UIA / WinEvent notifications from a target app and streams them to a sink.
/// Runs its own STA message-pump thread and unregisters all handlers on stop.
/// </summary>
internal interface IUiEventWatcher
{
    /// <summary>
    /// Begin listening. <paramref name="onEvent"/> is invoked once per observed event (serialized on
    /// the watcher's pump thread). Completes when the duration elapses, <c>maxEvents</c> is reached,
    /// or <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<UiWatchOutcome> WatchAsync(
        UiSessionInfo session,
        UiWatchRequest request,
        Action<UiWatchEvent> onEvent,
        CancellationToken ct);
}

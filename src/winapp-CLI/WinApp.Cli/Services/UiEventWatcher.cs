// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Real <see cref="IUiEventWatcher"/> that listens for UIA automation events and Win32 WinEvents.
/// </summary>
/// <remarks>
/// A dedicated STA thread creates its own <c>CUIAutomation8</c>, registers the requested handlers,
/// installs a <c>SetWinEventHook</c> for window open/close, and runs a message pump. Registration and
/// event delivery all happen on that single thread, so the sink is invoked serially (no locking).
/// All handlers are torn down via <c>RemoveAllEventHandlers</c> + <c>UnhookWinEvent</c> on stop.
/// <para>
/// AOT notes: the UIA handler sinks are <c>[GeneratedComClass]</c> CCWs over the CsWin32
/// <c>[GeneratedComInterface]</c> handler interfaces, and the WinEvent callback is a static
/// <c>[UnmanagedCallersOnly]</c> function pointer — both avoid runtime marshalling.
/// </para>
/// </remarks>
internal sealed partial class UiEventWatcher : IUiEventWatcher
{
    private const uint WM_QUIT = 0x0012;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const int OBJID_WINDOW = 0;
    private const int TreeScopeSubtree = 7; // TreeScope_Subtree

    // Only one watch runs at a time per process; the unmanaged WinEvent callback routes to it.
    private static UiEventWatcher? s_active;

    private readonly ILogger<UiEventWatcher> _logger;

    private IUIAutomation? _automation;
    private Action<UiWatchEvent> _onEvent = static _ => { };
    private UiWatchRequest _request = null!;
    private int _targetPid;
    private long _targetHwnd;
    private uint _pumpThreadId;
    private int _count;
    private volatile bool _stopping;
    private readonly WindowLifecycleCoalescer _windowCoalescer = new();

    public UiEventWatcher(ILogger<UiEventWatcher> logger)
    {
        _logger = logger;
    }

    public Task<UiWatchOutcome> WatchAsync(
        UiSessionInfo session,
        UiWatchRequest request,
        Action<UiWatchEvent> onEvent,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<UiWatchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        _request = request;
        _onEvent = onEvent;
        _targetPid = session.ProcessId;
        _targetHwnd = session.WindowHandle;
        _count = 0;
        _stopping = false;

        var thread = new Thread(() =>
        {
            try
            {
                s_active = this;
                _pumpThreadId = PInvoke.GetCurrentThreadId();
                _automation = CUIAutomation8.CreateInstance<IUIAutomation>();

                // Duration/stopwatch are started inside RunPump *after* handler registration completes,
                // so the reported DurationMs reflects actual listening time, not COM/handler setup.
                var elapsedMs = RunPump(request, ct);
                tcs.TrySetResult(new UiWatchOutcome(_count, elapsedMs));
            }
            catch (Exception ex)
            {
                _logger.LogDebug("watch pump failed: {Message}", ex.Message);
                tcs.TrySetException(ex);
            }
            finally
            {
                s_active = null;
                _automation = null;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private long RunPump(UiWatchRequest request, CancellationToken ct)
    {
        var scope = ResolveScopeElement();
        var winEventHook = RegisterHandlers(scope);

        // Start the countdown and the reported stopwatch only after registration completes, so the
        // listen window and DurationMs reflect actual listening time (issue: setup time inflation).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ctReg = ct.Register(RequestStop);
        Timer? durationTimer = null;
        if (request.DurationSec > 0)
        {
            durationTimer = new Timer(_ => RequestStop(), null,
                TimeSpan.FromSeconds(request.DurationSec), Timeout.InfiniteTimeSpan);
        }

        try
        {
            while (true)
            {
                var result = PInvoke.GetMessage(out var msg, HWND.Null, 0, 0);
                if (result.Value == 0 || result.Value == -1)
                {
                    break; // WM_QUIT (0) or error (-1)
                }

                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }
        }
        finally
        {
            sw.Stop();
            durationTimer?.Dispose();
            try { _automation?.RemoveAllEventHandlers(); } catch { /* best-effort teardown */ }
            winEventHook?.Dispose();
        }

        return sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// Resolve the UIA element that scopes UIA event registration: the selector-resolved element (when
    /// provided), else the target window. Returns <see langword="null"/> when no window handle is
    /// available (e.g. a process with no windows) — never falls back to the all-process desktop root.
    /// The command validates up front that element-scoped events always have a window, so a null scope
    /// here means only window open/close (process-scoped WinEvents) were requested.
    /// </summary>
    private IUIAutomationElement? ResolveScopeElement()
    {
        if (_targetHwnd == 0)
        {
            // No window to anchor a UIA subtree. Never fall back to the desktop root (all processes).
            return null;
        }

        // Let a failure here propagate: the target window vanished between resolution and watch start,
        // which the command surfaces via its generic error path (better than watching the desktop).
        var windowRoot = _automation!.ElementFromHandle(new HWND((nint)_targetHwnd));

        // Narrow to the selector-resolved element's live subtree when a selector was supplied.
        var scopeElement = _request.ScopeElement;
        if (scopeElement is null)
        {
            return windowRoot;
        }

        var narrowed = FindScopeElement(windowRoot, scopeElement);
        if (narrowed is not null)
        {
            return narrowed;
        }

        // The element existed when the command resolved it but is gone now (UI raced). Still process-safe
        // to watch the whole window; log and continue rather than watch the desktop or crash.
        _logger.LogDebug("watch selector element vanished before registration; scoping to window instead");
        return windowRoot;
    }

    /// <summary>Re-resolve a command-side <see cref="UiElement"/> snapshot to a live UIA element within the target window.</summary>
    private IUIAutomationElement? FindScopeElement(IUIAutomationElement windowRoot, UiElement element)
    {
        try
        {
            if (!string.IsNullOrEmpty(element.AutomationId))
            {
                var condition = _automation!.CreatePropertyCondition(
                    UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, ComVariant.Create(element.AutomationId));
                var byAid = windowRoot.FindFirst(TreeScope.TreeScope_Subtree, condition);
                if (byAid is not null) { return byAid; }
            }

            if (!string.IsNullOrEmpty(element.Name))
            {
                var condition = _automation!.CreatePropertyCondition(
                    UIA_PROPERTY_ID.UIA_NamePropertyId, ComVariant.Create(element.Name));
                var byName = windowRoot.FindFirst(TreeScope.TreeScope_Subtree, condition);
                if (byName is not null) { return byName; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("watch scope element re-resolution failed: {Message}", ex.Message);
        }

        return null;
    }

    private unsafe UnhookWinEventSafeHandle? RegisterHandlers(IUIAutomationElement? scope)
    {
        var auto = _automation!;
        var events = _request.Events;

        if (scope is not null && events.Contains(UiWatchEvents.Focus))
        {
            // Focus changes are inherently global in UIA (there is no subtree-scoped focus handler), so
            // even when a selector narrowed the scope, focus events are only process-filtered — not
            // limited to the selector's subtree. Filtering happens in OnFocusChanged via MatchesTargetProcess.
            try { auto.AddFocusChangedEventHandler(null, new FocusHandler(this)); }
            catch (Exception ex) { _logger.LogDebug("focus handler failed: {Message}", ex.Message); }
        }

        if (scope is not null)
        {
            var autoHandler = new AutomationHandler(this);
            RegisterAutomationEvent(events, UiWatchEvents.Invoke, UIA_EVENT_ID.UIA_Invoke_InvokedEventId, scope, autoHandler);
            RegisterAutomationEvent(events, UiWatchEvents.Selection, UIA_EVENT_ID.UIA_SelectionItem_ElementSelectedEventId, scope, autoHandler);
            RegisterAutomationEvent(events, UiWatchEvents.Selection, UIA_EVENT_ID.UIA_Selection_InvalidatedEventId, scope, autoHandler);
            RegisterAutomationEvent(events, UiWatchEvents.TextChanged, UIA_EVENT_ID.UIA_Text_TextChangedEventId, scope, autoHandler);
            RegisterAutomationEvent(events, UiWatchEvents.Notification, UIA_EVENT_ID.UIA_NotificationEventId, scope, autoHandler);
            RegisterAutomationEvent(events, UiWatchEvents.LiveRegion, UIA_EVENT_ID.UIA_LiveRegionChangedEventId, scope, autoHandler);

            if (events.Contains(UiWatchEvents.StructureChanged))
            {
                try { auto.AddStructureChangedEventHandler(scope, (TreeScope)TreeScopeSubtree, null, new StructureHandler(this)); }
                catch (Exception ex) { _logger.LogDebug("structure handler failed: {Message}", ex.Message); }
            }

            if (events.Contains(UiWatchEvents.PropertyChanged))
            {
                try
                {
                    Span<UIA_PROPERTY_ID> props =
                    [
                        UIA_PROPERTY_ID.UIA_NamePropertyId,
                        UIA_PROPERTY_ID.UIA_ValueValuePropertyId,
                        UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId,
                    ];
                    auto.AddPropertyChangedEventHandlerNativeArray(
                        scope, (TreeScope)TreeScopeSubtree, null, new PropertyHandler(this), props);
                }
                catch (Exception ex) { _logger.LogDebug("property handler failed: {Message}", ex.Message); }
            }
        }

        // Window open/close arrive via WinEvents, filtered to the target process.
        if (events.Contains(UiWatchEvents.WindowOpen) || events.Contains(UiWatchEvents.WindowClose))
        {
            try
            {
                return PInvoke.SetWinEventHook(
                    EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE, null, &WinEventProc,
                    (uint)_targetPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            }
            catch (Exception ex) { _logger.LogDebug("SetWinEventHook failed: {Message}", ex.Message); }
        }

        return null;
    }

    private void RegisterAutomationEvent(
        IReadOnlyList<string> events, string eventName, UIA_EVENT_ID eventId,
        IUIAutomationElement scope, AutomationHandler handler)
    {
        if (!events.Contains(eventName)) { return; }
        try
        {
            _automation!.AddAutomationEventHandler(eventId, scope, (TreeScope)TreeScopeSubtree, null, handler);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("automation handler {Event} failed: {Message}", eventName, ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void WinEventProc(
        HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild, uint idThread, uint time)
    {
        var self = s_active;
        if (self is null) { return; }
        try { self.OnWinEvent(eventId, hwnd, idObject, idChild); }
        catch { /* never let an exception cross the native boundary */ }
    }

    private unsafe void OnWinEvent(uint eventId, HWND hwnd, int idObject, int idChild)
    {
        // Only care about top-level window objects, not their child accessibles.
        if (idObject != OBJID_WINDOW || idChild != 0) { return; }

        var isOpen = eventId is EVENT_OBJECT_CREATE or EVENT_OBJECT_SHOW;
        var isClose = eventId is EVENT_OBJECT_DESTROY or EVENT_OBJECT_HIDE;

        if (isOpen && !_request.Events.Contains(UiWatchEvents.WindowOpen)) { return; }
        if (isClose && !_request.Events.Contains(UiWatchEvents.WindowClose)) { return; }
        if (!isOpen && !isClose) { return; }

        // Collapse the CREATE+SHOW / DESTROY+HIDE pairs so one logical window open/close emits once.
        if (!_windowCoalescer.ShouldEmit((long)(nint)hwnd.Value, isOpen, DateTime.UtcNow.Ticks)) { return; }

        var element = Describe(SafeElementFromHandle(hwnd));
        Emit(isOpen ? UiWatchEvents.WindowOpen : UiWatchEvents.WindowClose, element,
            $"hwnd=0x{(nint)hwnd.Value:X}");
    }

    internal void OnFocusChanged(IUIAutomationElement sender)
    {
        if (!MatchesTargetProcess(sender)) { return; }
        Emit(UiWatchEvents.Focus, Describe(sender), null);
    }

    internal void OnAutomationEvent(IUIAutomationElement sender, UIA_EVENT_ID eventId)
    {
        if (!MatchesTargetProcess(sender)) { return; }
        var (name, ok) = MapAutomationEvent(eventId);
        if (!ok) { return; }
        Emit(name, Describe(sender), eventId.ToString());
    }

    internal void OnPropertyChanged(IUIAutomationElement sender, UIA_PROPERTY_ID propertyId)
    {
        if (!MatchesTargetProcess(sender)) { return; }
        var propName = propertyId switch
        {
            UIA_PROPERTY_ID.UIA_NamePropertyId => "Name",
            UIA_PROPERTY_ID.UIA_ValueValuePropertyId => "Value",
            UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId => "ToggleState",
            _ => propertyId.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(_request.Property) &&
            !string.Equals(_request.Property, propName, StringComparison.OrdinalIgnoreCase))
        {
            return; // property filter excluded this change
        }

        Emit(UiWatchEvents.PropertyChanged, Describe(sender), propName);
    }

    internal void OnStructureChanged(IUIAutomationElement sender, StructureChangeType changeType)
    {
        if (!MatchesTargetProcess(sender)) { return; }
        Emit(UiWatchEvents.StructureChanged, Describe(sender), changeType.ToString());
    }

    private static (string Name, bool Ok) MapAutomationEvent(UIA_EVENT_ID eventId) => eventId switch
    {
        UIA_EVENT_ID.UIA_Invoke_InvokedEventId => (UiWatchEvents.Invoke, true),
        UIA_EVENT_ID.UIA_SelectionItem_ElementSelectedEventId => (UiWatchEvents.Selection, true),
        UIA_EVENT_ID.UIA_Selection_InvalidatedEventId => (UiWatchEvents.Selection, true),
        UIA_EVENT_ID.UIA_Text_TextChangedEventId => (UiWatchEvents.TextChanged, true),
        UIA_EVENT_ID.UIA_NotificationEventId => (UiWatchEvents.Notification, true),
        UIA_EVENT_ID.UIA_LiveRegionChangedEventId => (UiWatchEvents.LiveRegion, true),
        _ => ("", false),
    };

    private bool MatchesTargetProcess(IUIAutomationElement element)
    {
        if (_targetPid == 0) { return true; }
        try { return element.get_CurrentProcessId() == _targetPid; }
        catch { return false; }
    }

    private IUIAutomationElement? SafeElementFromHandle(HWND hwnd)
    {
        try { return _automation!.ElementFromHandle(hwnd); }
        catch { return null; }
    }

    private static UiWatchElement? Describe(IUIAutomationElement? element)
    {
        if (element is null) { return null; }
        try
        {
            return new UiWatchElement
            {
                Name = SafeBstr(() => element.get_CurrentName()),
                ControlType = SafeBstr(() => element.get_CurrentLocalizedControlType()),
                Selector = SafeBstr(() => element.get_CurrentAutomationId()),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeBstr(Func<BSTR> getter)
    {
        try
        {
            var val = getter().ToString();
            return string.IsNullOrEmpty(val) ? null : val;
        }
        catch
        {
            return null;
        }
    }

    private void Emit(string eventName, UiWatchElement? element, string? detail)
    {
        if (_stopping) { return; }

        var evt = new UiWatchEvent
        {
            Ts = DateTime.UtcNow.ToString("O"),
            Event = eventName,
            Element = element,
            Detail = detail,
        };

        try { _onEvent(evt); }
        catch (Exception ex) { _logger.LogDebug("watch sink threw: {Message}", ex.Message); }

        _count++;
        if (_request.MaxEvents > 0 && _count >= _request.MaxEvents)
        {
            RequestStop();
        }
    }

    private void RequestStop()
    {
        if (_stopping) { return; }
        _stopping = true;
        if (_pumpThreadId != 0)
        {
            PInvoke.PostThreadMessage(_pumpThreadId, WM_QUIT, default, default);
        }
    }

    // ---- [GeneratedComClass] sinks: thin CCW adapters over the CsWin32 handler interfaces ----

    [GeneratedComClass]
    private sealed partial class FocusHandler(UiEventWatcher owner) : IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender) => owner.OnFocusChanged(sender);
    }

    [GeneratedComClass]
    private sealed partial class AutomationHandler(UiEventWatcher owner) : IUIAutomationEventHandler
    {
        public void HandleAutomationEvent(IUIAutomationElement sender, UIA_EVENT_ID eventId)
            => owner.OnAutomationEvent(sender, eventId);
    }

    [GeneratedComClass]
    private sealed partial class PropertyHandler(UiEventWatcher owner) : IUIAutomationPropertyChangedEventHandler
    {
        public void HandlePropertyChangedEvent(IUIAutomationElement sender, UIA_PROPERTY_ID propertyId, ComVariant newValue)
            => owner.OnPropertyChanged(sender, propertyId);
    }

    [GeneratedComClass]
    private sealed unsafe partial class StructureHandler(UiEventWatcher owner) : IUIAutomationStructureChangedEventHandler
    {
        public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, Windows.Win32.System.Com.SAFEARRAY* runtimeId)
            => owner.OnStructureChanged(sender, changeType);
    }
}

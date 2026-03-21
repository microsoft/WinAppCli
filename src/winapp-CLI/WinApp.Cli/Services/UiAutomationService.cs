// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// UIA backend using Windows UI Automation COM APIs via CsWin32.
/// Provides cross-process element tree inspection and pattern-based interaction.
/// </summary>
internal sealed class UiAutomationService : IUiAutomationService
{
    private readonly ILogger<UiAutomationService> _logger;
    private readonly IUIAutomation _automation;
    private int _nextElementId;

    public UiAutomationService(ILogger<UiAutomationService> logger)
    {
        _logger = logger;
        _automation = CUIAutomation8.CreateInstance<IUIAutomation>();
    }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery)
    {
        return EnumerateWindows((pid, title) =>
            title.Length > 0 && title.Contains(titleQuery, StringComparison.OrdinalIgnoreCase));
    }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int targetPid)
    {
        return EnumerateWindows((pid, title) => pid == targetPid);
    }

    private static List<(nint Hwnd, int Pid, string Title)> EnumerateWindows(Func<int, string, bool> filter)
    {
        var results = new List<(nint, int, string)>();
        var hwnd = Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            hwnd = Windows.Win32.PInvoke.FindWindowEx(
                Windows.Win32.Foundation.HWND.Null, hwnd, null, (string?)null);
            if (hwnd.IsNull)
            {
                break;
            }

            if (!Windows.Win32.PInvoke.IsWindowVisible(hwnd))
            {
                continue;
            }

            unsafe
            {
                uint pid = 0;
                Windows.Win32.PInvoke.GetWindowThreadProcessId(hwnd, &pid);

                // Allocate buffer outside the hot path (CA2014: no stackalloc in loop)
                var titleChars = new char[512];
                fixed (char* buffer = titleChars)
                {
                    var len = Windows.Win32.PInvoke.GetWindowText(hwnd, buffer, 512);
                    var title = len > 0 ? new string(buffer, 0, len) : "";

                    if (filter((int)pid, title))
                    {
                        results.Add(((nint)hwnd.Value, (int)pid, title));
                    }
                }
            }
        }
        return results;
    }

    public Task<UiElement[]> InspectAsync(UiSessionInfo session, string? elementId, int depth, CancellationToken ct)
    {
        _logger.LogDebug("Inspecting process {Pid} at depth {Depth}", session.ProcessId, depth);
        _nextElementId = 0;

        var root = GetRootElement(session);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        var elements = new List<UiElement>();
        WalkTree(root, depth, 0, "", elements);
return Task.FromResult(elements.ToArray());
    }

    public Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct)
    {
        _logger.LogDebug("Inspecting ancestors of {ElementId}", elementId);
        _nextElementId = 0;

        // First re-find the element
        if (session.Elements is null || !session.Elements.TryGetValue(elementId, out var cached))
        {
            throw new InvalidOperationException($"Element {elementId} not found in session cache. Run 'inspect' or 'search' first.");
        }

        var root = GetRootElement(session);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        // Find the target element
        var target = ResolveComElement(session, new UiElement
        {
            Id = elementId,
            AutomationId = cached.AutomationId,
            Name = cached.Name,
            Type = cached.Type
        });

        if (target is null)
        {
            throw new InvalidOperationException($"Element {elementId} is stale. Re-run 'inspect' or 'search'.");
        }

        // Walk up via TreeWalker
        var ancestors = new List<UiElement>();
        var walker = _automation.get_ControlViewWalker();
        var current = target;

        // Add the target element itself first
        ancestors.Add(ToUiElement(current, ""));

        while (true)
        {
            IUIAutomationElement? parent;
            try
            {
                parent = walker.GetParentElement(current);
            }
            catch
            {
                break;
            }

            if (parent is null)
            {
                break;
            }

            // Stop at desktop root (PID 0 or no process)
            try
            {
                var rect = parent.get_CurrentBoundingRectangle();
                // Check if this is the desktop root (has no meaningful parent)
                var parentParent = walker.GetParentElement(parent);
                if (parentParent is null)
                {
                    break;
                }
            }
            catch
            {
                break;
            }

            ancestors.Add(ToUiElement(parent, ""));
            current = parent;
        }

        // Reverse so root is first, target is last
        ancestors.Reverse();
        return Task.FromResult(ancestors.ToArray());
    }

    public Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct)
    {
        _logger.LogDebug("Searching in process {Pid}", session.ProcessId);
        _nextElementId = 0;

        var root = GetRootElement(session);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        var condition = BuildCondition(selector);
        if (condition is null)
        {
return Task.FromResult<UiElement[]>([]);
        }

        IUIAutomationElementArray? found;
        try
        {
            found = root.FindAll(TreeScope.TreeScope_Descendants, condition);
        }
        finally
        {

}

        if (found is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        var length = found.get_Length();
        var count = Math.Min(length, maxResults);
        var results = new UiElement[count];
        for (var i = 0; i < count; i++)
        {
            var el = found.GetElement(i);
            results[i] = ToUiElement(el, "");
}
return Task.FromResult(results);
    }

    public Task<UiElement?> FindSingleElementAsync(UiSessionInfo session, SelectorExpression selector, CancellationToken ct)
    {
        _logger.LogDebug("Finding single element in process {Pid}", session.ProcessId);

        var root = GetRootElement(session);
        if (root is null)
        {
            return Task.FromResult<UiElement?>(null);
        }

        var condition = BuildCondition(selector);
        if (condition is null)
        {
return Task.FromResult<UiElement?>(null);
        }

        IUIAutomationElementArray? found;
        try
        {
            found = root.FindAll(TreeScope.TreeScope_Descendants, condition);
        }
        finally
        {

}

        if (found is null || found.get_Length() == 0)
        {
            return Task.FromResult<UiElement?>(null);
        }

        if (found.get_Length() > 1)
        {
            var matchCount = found.get_Length();
            throw new InvalidOperationException(
                $"Selector matched {matchCount} elements. Narrow your selector or use 'search' to list them.");
        }

        var element = found.GetElement(0);
        var result = ToUiElement(element, "");

return Task.FromResult<UiElement?>(result);
    }

    public Task<UiElement?> FindElementByIdAsync(UiSessionInfo session, string elementId, CancellationToken ct)
    {
        _logger.LogDebug("Finding element by cached ID {ElementId}", elementId);

        if (session.Elements is null || !session.Elements.TryGetValue(elementId, out var cached))
        {
            return Task.FromResult<UiElement?>(null);
        }

        var root = GetRootElement(session);
        if (root is null)
        {
            return Task.FromResult<UiElement?>(null);
        }

        var selector = cached.AutomationId is not null
            ? new SelectorExpression { AutomationId = cached.AutomationId, Type = cached.Type }
            : new SelectorExpression { Name = cached.Name, Type = cached.Type };

        var condition = BuildCondition(selector);
        if (condition is null)
        {
            return Task.FromResult<UiElement?>(null);
        }

        var found = root.FindAll(TreeScope.TreeScope_Descendants, condition);
        if (found is null || found.get_Length() == 0)
        {
            return Task.FromResult<UiElement?>(null);
        }

        // Single match — use it
        if (found.get_Length() == 1)
        {
            var result = ToUiElement(found.GetElement(0), "");
            return Task.FromResult<UiElement?>(result);
        }

        // Multiple matches — pick the one closest to the cached position
        _logger.LogDebug("Found {Count} matches for {Id}, disambiguating by position ({X},{Y})",
            found.get_Length(), elementId, cached.X, cached.Y);

        IUIAutomationElement? best = null;
        double bestDist = double.MaxValue;
        for (int i = 0; i < found.get_Length(); i++)
        {
            var el = found.GetElement(i);
            var r = el.get_CurrentBoundingRectangle();
            var dx = r.left - cached.X;
            var dy = r.top - cached.Y;
            var dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                best = el;
                bestDist = dist;
            }
        }

        var match = ToUiElement(best!, "");
        return Task.FromResult<UiElement?>(match);
    }

    public Task<Dictionary<string, object?>> GetPropertiesAsync(UiSessionInfo session, UiElement element, string? propertyName, CancellationToken ct)
    {
        var props = new Dictionary<string, object?>
        {
            ["Name"] = element.Name,
            ["AutomationId"] = element.AutomationId,
            ["ControlType"] = element.Type,
            ["ClassName"] = element.ClassName,
            ["IsEnabled"] = element.IsEnabled,
            ["IsOffscreen"] = element.IsOffscreen,
            ["BoundingRectangle"] = $"{element.X},{element.Y},{element.Width},{element.Height}"
        };

        if (propertyName is not null)
        {
            if (props.TryGetValue(propertyName, out var val))
            {
                return Task.FromResult(new Dictionary<string, object?> { [propertyName] = val });
            }
            return Task.FromResult(new Dictionary<string, object?> { [propertyName] = null });
        }

        return Task.FromResult(props);
    }

    public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiSessionInfo session, string? elementId, CancellationToken ct)
    {
        _logger.LogDebug("Taking screenshot of process {Pid}", session.ProcessId);

        var root = GetRootElement(session);
        if (root is null)
        {
            throw new InvalidOperationException($"No UIA window found for {session.ProcessName} (PID {session.ProcessId}).");
        }

        // Get the actual window title from UIA (not session cache, which may be stale)
        var rootName = SafeGetBstr(() => root.get_CurrentName());
        if (rootName is not null)
        {
            session.WindowTitle = rootName;
        }

        var hwnd = root.get_CurrentNativeWindowHandle();
        if (hwnd.IsNull)
        {
            throw new InvalidOperationException($"No native window handle for {session.ProcessName}. Is the window visible?");
        }

        // Check if window is minimized
        if (Windows.Win32.PInvoke.IsIconic(hwnd))
        {
            // Restore window so we can capture it
            Windows.Win32.PInvoke.ShowWindow(hwnd, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
            // Brief wait for window to restore
            Thread.Sleep(300);
        }

        // Get window dimensions
        Windows.Win32.PInvoke.GetWindowRect(hwnd, out var rect);
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Window has zero size. Is it minimized?");
        }

        // Capture via PrintWindow + GDI
        var hdcWindow = Windows.Win32.PInvoke.GetDC(hwnd);
        var hdcMem = Windows.Win32.PInvoke.CreateCompatibleDC(hdcWindow);
        var hBitmap = Windows.Win32.PInvoke.CreateCompatibleBitmap(hdcWindow, width, height);

        unsafe
        {
            var hOld = Windows.Win32.PInvoke.SelectObject(hdcMem, *(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);

            // PW_RENDERFULLCONTENT = 2
            Windows.Win32.PInvoke.PrintWindow(hwnd, hdcMem, (Windows.Win32.Storage.Xps.PRINT_WINDOW_FLAGS)2);

            Windows.Win32.PInvoke.SelectObject(hdcMem, hOld);

            // Extract pixel data
            var bmi = new Windows.Win32.Graphics.Gdi.BITMAPINFO
            {
                bmiHeader = new Windows.Win32.Graphics.Gdi.BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(Windows.Win32.Graphics.Gdi.BITMAPINFOHEADER),
                    biWidth = width,
                    biHeight = -height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            var pixelData = new byte[width * height * 4];
            fixed (byte* pPixels = pixelData)
            {
                Windows.Win32.PInvoke.GetDIBits(hdcWindow, hBitmap, 0, (uint)height, pPixels, &bmi,
                    Windows.Win32.Graphics.Gdi.DIB_USAGE.DIB_RGB_COLORS);
            }

            // Cleanup GDI
            Windows.Win32.PInvoke.DeleteObject(*(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);
            Windows.Win32.PInvoke.DeleteDC(hdcMem);
            Windows.Win32.PInvoke.ReleaseDC(hwnd, hdcWindow);

            return Task.FromResult((pixelData, width, height));
        }
    }

    public Task<string> InvokeAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        _logger.LogDebug("Invoking element {ElementId}", element.Id);

        var comElement = ResolveComElement(session, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        // Try InvokePattern
        try
        {
            var pattern = (IUIAutomationInvokePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_InvokePatternId);
            pattern.Invoke();
            return Task.FromResult("InvokePattern");
        }
        catch { }

        // Try TogglePattern
        try
        {
            var pattern = (IUIAutomationTogglePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
            pattern.Toggle();
            return Task.FromResult("TogglePattern");
        }
        catch { }

        // Try SelectionItemPattern
        try
        {
            var pattern = (IUIAutomationSelectionItemPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionItemPatternId);
            pattern.Select();
            return Task.FromResult("SelectionItemPattern");
        }
        catch { }

        // Try ExpandCollapsePattern
        try
        {
            var pattern = (IUIAutomationExpandCollapsePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
            pattern.Expand();
            return Task.FromResult("ExpandCollapsePattern");
        }
        catch { }

        throw new InvalidOperationException(
            $"Element {element.Id} ({element.Type}) does not support any invoke pattern. " +
            "Supported: InvokePattern, TogglePattern, SelectionItemPattern, ExpandCollapsePattern.");
    }

    public Task SetValueAsync(UiSessionInfo session, UiElement element, string text, CancellationToken ct)
    {
        _logger.LogDebug("Setting value on element {ElementId}", element.Id);

        var comElement = ResolveComElement(session, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        try
        {
            var pattern = (IUIAutomationValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
            unsafe
            {
                fixed (char* pText = text)
                {
                    var bstr = new Windows.Win32.Foundation.BSTR(pText);
                    pattern.SetValue(bstr);
                }
            }
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Element {element.Id} ({element.Type}) does not support ValuePattern. " +
                "Only editable controls (TextBox, ComboBox, etc.) support set-value.", ex);
        }
    }

    public Task FocusAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        _logger.LogDebug("Focusing element {ElementId}", element.Id);

        var comElement = ResolveComElement(session, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        comElement.SetFocus();
        return Task.CompletedTask;
    }

    public Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        _logger.LogDebug("Scrolling element {ElementId} into view", element.Id);

        var comElement = ResolveComElement(session, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        try
        {
            var pattern = (IUIAutomationScrollItemPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollItemPatternId);
            pattern.ScrollIntoView();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Element {element.Id} ({element.Type}) does not support ScrollItemPattern.", ex);
        }
    }

    // --- Private helpers ---

    /// <summary>
    /// Re-finds a live COM UIA element from our serialized UiElement model.
    /// Uses AutomationId (most stable), then Name+Type, to locate the element.
    /// </summary>
    private IUIAutomationElement? ResolveComElement(UiSessionInfo session, UiElement element)
    {
        var root = GetRootElement(session);
        if (root is null)

        {

            return null;

        }

        // Try AutomationId first (most stable)
        if (element.AutomationId is not null)
        {
            var condition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                ComVariant.Create(element.AutomationId));
            var found = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            if (found is not null)

            {

                return found;

            }
        }

        // Try Name + ControlType
        if (element.Name is not null)
        {
            var typeId = MapControlType(element.Type);
            var nameCondition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_NamePropertyId,
                ComVariant.Create(element.Name));

            IUIAutomationCondition condition;
            if (typeId != 0)
            {
                var typeCondition = _automation.CreatePropertyCondition(
                    UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
                    ComVariant.Create(typeId));
                condition = _automation.CreateAndCondition(nameCondition, typeCondition);
            }
            else
            {
                condition = nameCondition;
            }

            var found = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            if (found is not null)

            {

                return found;

            }
        }

        return null;
    }

    private IUIAutomationElement? GetRootElement(UiSessionInfo session)
    {
        // If we have a specific window handle, use it directly
        if (session.WindowHandle != 0)
        {
            try
            {
                var hwnd = new Windows.Win32.Foundation.HWND((nint)session.WindowHandle);
                var element = _automation.ElementFromHandle(hwnd);
                if (element is not null)
                {
                    var name = SafeGetBstr(() => element.get_CurrentName());
                    _logger.LogDebug("ElementFromHandle(stored HWND {Hwnd}): \"{Name}\"", session.WindowHandle, name ?? "(null)");
                    return element;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Stored HWND {Hwnd} failed: {Error}", session.WindowHandle, ex.Message);
            }
        }

        var root = _automation.GetRootElement();
        if (root is null)

        {

            return null;

        }

        var condition = _automation.CreatePropertyCondition(
            UIA_PROPERTY_ID.UIA_ProcessIdPropertyId,
            ComVariant.Create(session.ProcessId));

        var all = root.FindAll(TreeScope.TreeScope_Children, condition);
        var count = all?.get_Length() ?? 0;
        _logger.LogDebug("UIA FindAll for PID {Pid}: {Count} top-level elements", session.ProcessId, count);

        if (count > 0)
        {
            // Log all found elements
            for (int i = 0; i < count; i++)
            {
                var el = all!.GetElement(i);
                var name = SafeGetBstr(() => el.get_CurrentName());
                var rect = el.get_CurrentBoundingRectangle();
                _logger.LogDebug("  [{Index}] \"{Name}\" bounds=({L},{T},{R},{B})", i, name ?? "(null)", rect.left, rect.top, rect.right, rect.bottom);
            }

            if (count == 1)
            {
                return all!.GetElement(0);
            }

            // Multiple top-level elements — try matching by AppQuery (user's --app value)
            var titleQuery = session.AppQuery;
            if (titleQuery is not null && !int.TryParse(titleQuery, out _))
            {
                for (int i = 0; i < count; i++)
                {
                    var el = all!.GetElement(i);
                    var name = SafeGetBstr(() => el.get_CurrentName());
                    if (name is not null && name.Contains(titleQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Matched window by query \"{Query}\": \"{Name}\"", titleQuery, name);
                        return el;
                    }
                }
            }

            // Fall back to largest bounds
            IUIAutomationElement? best = null;
            long bestArea = 0;
            for (int i = 0; i < count; i++)
            {
                var el = all!.GetElement(i);
                var r = el.get_CurrentBoundingRectangle();
                long area = (long)(r.right - r.left) * (r.bottom - r.top);
                if (area > bestArea) { best = el; bestArea = area; }
            }
            return best ?? all!.GetElement(0);
        }

        // PID-based search failed — fallback: find HWND via Process and use ElementFromHandle
        _logger.LogDebug("PID search returned 0 elements, trying ElementFromHandle fallback");
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(session.ProcessId);
            if (proc.MainWindowHandle != 0)
            {
                var hwnd = new Windows.Win32.Foundation.HWND(proc.MainWindowHandle);
                var element = _automation.ElementFromHandle(hwnd);
                if (element is not null)
                {
                    var name = SafeGetBstr(() => element.get_CurrentName());
                    _logger.LogDebug("ElementFromHandle found: \"{Name}\"", name ?? "(null)");
                    return element;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("ElementFromHandle failed: {Error}", ex.Message);
        }

        return null;
    }

    private IUIAutomationCondition? BuildCondition(SelectorExpression selector)
    {
        IUIAutomationCondition? condition = null;

        if (selector.AutomationId is not null)
        {
            condition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                ComVariant.Create(selector.AutomationId));
        }

        if (selector.Name is not null)
        {
            var nameCondition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_NamePropertyId,
                ComVariant.Create(selector.Name));
            condition = condition is not null
                ? _automation.CreateAndCondition(condition, nameCondition)
                : nameCondition;
        }

        if (selector.Type is not null)
        {
            var typeId = MapControlType(selector.Type);
            if (typeId != 0)
            {
                var typeCondition = _automation.CreatePropertyCondition(
                    UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
                    ComVariant.Create(typeId));
                condition = condition is not null
                    ? _automation.CreateAndCondition(condition, typeCondition)
                    : typeCondition;
            }
        }

        return condition;
    }

    private void WalkTree(IUIAutomationElement element, int maxDepth, int currentDepth, string path, List<UiElement> results)
    {
        var uiElement = ToUiElement(element, path);
        results.Add(uiElement);

        if (currentDepth >= maxDepth)


        {


            return;


        }

        var walker = _automation.get_ControlViewWalker();
        var child = walker.GetFirstChildElement(element);
        var childIndex = 0;

        while (child is not null)
        {
            var childPath = string.IsNullOrEmpty(path) ? $"/{childIndex}" : $"{path}/{childIndex}";
            WalkTree(child, maxDepth, currentDepth + 1, childPath, results);

            IUIAutomationElement? next;
            try
            {
                next = walker.GetNextSiblingElement(child);
            }
            catch
            {
                next = null;
            }
child = next;
            childIndex++;
        }
}

    private UiElement ToUiElement(IUIAutomationElement element, string path)
    {
        var id = $"e{_nextElementId++}";
        var rect = element.get_CurrentBoundingRectangle();

        return new UiElement
        {
            Id = id,
            Type = GetControlTypeName(element.get_CurrentControlType()),
            Name = SafeGetBstr(() => element.get_CurrentName()),
            AutomationId = SafeGetBstr(() => element.get_CurrentAutomationId()),
            ClassName = SafeGetBstr(() => element.get_CurrentClassName()),
            IsEnabled = element.get_CurrentIsEnabled(),
            IsOffscreen = element.get_CurrentIsOffscreen(),
            X = rect.left,
            Y = rect.top,
            Width = rect.right - rect.left,
            Height = rect.bottom - rect.top,
        };
    }

    private static string? SafeGetBstr(Func<Windows.Win32.Foundation.BSTR> getter)
    {
        try
        {
            var bstr = getter();
            var val = bstr.ToString();
            return string.IsNullOrEmpty(val) ? null : val;
        }
        catch
        {
            return null;
        }
    }

    private static string GetControlTypeName(UIA_CONTROLTYPE_ID controlType) => controlType switch
    {
        UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId => "Button",
        UIA_CONTROLTYPE_ID.UIA_CalendarControlTypeId => "Calendar",
        UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId => "CheckBox",
        UIA_CONTROLTYPE_ID.UIA_ComboBoxControlTypeId => "ComboBox",
        UIA_CONTROLTYPE_ID.UIA_EditControlTypeId => "Edit",
        UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId => "Hyperlink",
        UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId => "Image",
        UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId => "ListItem",
        UIA_CONTROLTYPE_ID.UIA_ListControlTypeId => "List",
        UIA_CONTROLTYPE_ID.UIA_MenuControlTypeId => "Menu",
        UIA_CONTROLTYPE_ID.UIA_MenuBarControlTypeId => "MenuBar",
        UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId => "MenuItem",
        UIA_CONTROLTYPE_ID.UIA_ProgressBarControlTypeId => "ProgressBar",
        UIA_CONTROLTYPE_ID.UIA_RadioButtonControlTypeId => "RadioButton",
        UIA_CONTROLTYPE_ID.UIA_ScrollBarControlTypeId => "ScrollBar",
        UIA_CONTROLTYPE_ID.UIA_SliderControlTypeId => "Slider",
        UIA_CONTROLTYPE_ID.UIA_SpinnerControlTypeId => "Spinner",
        UIA_CONTROLTYPE_ID.UIA_StatusBarControlTypeId => "StatusBar",
        UIA_CONTROLTYPE_ID.UIA_TabControlTypeId => "Tab",
        UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId => "TabItem",
        UIA_CONTROLTYPE_ID.UIA_TextControlTypeId => "Text",
        UIA_CONTROLTYPE_ID.UIA_ToolBarControlTypeId => "ToolBar",
        UIA_CONTROLTYPE_ID.UIA_ToolTipControlTypeId => "ToolTip",
        UIA_CONTROLTYPE_ID.UIA_TreeControlTypeId => "Tree",
        UIA_CONTROLTYPE_ID.UIA_TreeItemControlTypeId => "TreeItem",
        UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId => "Group",
        UIA_CONTROLTYPE_ID.UIA_ThumbControlTypeId => "Thumb",
        UIA_CONTROLTYPE_ID.UIA_DataGridControlTypeId => "DataGrid",
        UIA_CONTROLTYPE_ID.UIA_DataItemControlTypeId => "DataItem",
        UIA_CONTROLTYPE_ID.UIA_DocumentControlTypeId => "Document",
        UIA_CONTROLTYPE_ID.UIA_SplitButtonControlTypeId => "SplitButton",
        UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId => "Window",
        UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId => "Pane",
        UIA_CONTROLTYPE_ID.UIA_HeaderControlTypeId => "Header",
        UIA_CONTROLTYPE_ID.UIA_HeaderItemControlTypeId => "HeaderItem",
        UIA_CONTROLTYPE_ID.UIA_TableControlTypeId => "Table",
        UIA_CONTROLTYPE_ID.UIA_TitleBarControlTypeId => "TitleBar",
        UIA_CONTROLTYPE_ID.UIA_SeparatorControlTypeId => "Separator",
        UIA_CONTROLTYPE_ID.UIA_AppBarControlTypeId => "AppBar",
        UIA_CONTROLTYPE_ID.UIA_SemanticZoomControlTypeId => "SemanticZoom",
        _ => $"Unknown({(int)controlType})"
    };

    private static int MapControlType(string typeName) => typeName switch
    {
        "Button" => (int)UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId,
        "CheckBox" => (int)UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId,
        "ComboBox" => (int)UIA_CONTROLTYPE_ID.UIA_ComboBoxControlTypeId,
        "Edit" or "TextBox" => (int)UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
        "Hyperlink" => (int)UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId,
        "Image" => (int)UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId,
        "ListItem" => (int)UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId,
        "List" => (int)UIA_CONTROLTYPE_ID.UIA_ListControlTypeId,
        "Menu" => (int)UIA_CONTROLTYPE_ID.UIA_MenuControlTypeId,
        "MenuBar" => (int)UIA_CONTROLTYPE_ID.UIA_MenuBarControlTypeId,
        "MenuItem" => (int)UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId,
        "ProgressBar" => (int)UIA_CONTROLTYPE_ID.UIA_ProgressBarControlTypeId,
        "RadioButton" => (int)UIA_CONTROLTYPE_ID.UIA_RadioButtonControlTypeId,
        "ScrollBar" => (int)UIA_CONTROLTYPE_ID.UIA_ScrollBarControlTypeId,
        "Slider" => (int)UIA_CONTROLTYPE_ID.UIA_SliderControlTypeId,
        "Tab" => (int)UIA_CONTROLTYPE_ID.UIA_TabControlTypeId,
        "TabItem" => (int)UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId,
        "Text" or "TextBlock" => (int)UIA_CONTROLTYPE_ID.UIA_TextControlTypeId,
        "ToolBar" => (int)UIA_CONTROLTYPE_ID.UIA_ToolBarControlTypeId,
        "Tree" => (int)UIA_CONTROLTYPE_ID.UIA_TreeControlTypeId,
        "TreeItem" => (int)UIA_CONTROLTYPE_ID.UIA_TreeItemControlTypeId,
        "Group" => (int)UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId,
        "DataGrid" => (int)UIA_CONTROLTYPE_ID.UIA_DataGridControlTypeId,
        "Window" => (int)UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId,
        "Pane" => (int)UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId,
        "Table" => (int)UIA_CONTROLTYPE_ID.UIA_TableControlTypeId,
        "TitleBar" => (int)UIA_CONTROLTYPE_ID.UIA_TitleBarControlTypeId,
        _ => 0
    };
}

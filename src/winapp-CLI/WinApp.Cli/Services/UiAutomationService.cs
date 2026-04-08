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
internal sealed partial class UiAutomationService : IUiAutomationService
{
    private readonly ILogger<UiAutomationService> _logger;
    private readonly IUIAutomation _automation;
    private readonly ISelectorService _selectorService;

    public UiAutomationService(ILogger<UiAutomationService> logger, ISelectorService selectorService)
    {
        _logger = logger;
        _selectorService = selectorService;
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
        var nextElementId = 0;

        var root = GetRootElement(session);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        // If a selector is provided, scope the tree walk to that element
        IUIAutomationElement startElement = root;
        if (!string.IsNullOrEmpty(elementId))
        {
            IUIAutomationElement? target = null;

            // Try as a slug first
            var slugParsed = SlugGenerator.ParseSlug(elementId);
            if (slugParsed is not null)
            {
                var slugResult = FindElementBySlug(elementId, root);
                if (slugResult is not null)
                {
                    // Re-find the COM element
                    target = ResolveComElement(session, slugResult);
                }
            }
            else
            {
                // Try as a legacy selector
                var selectorService = _selectorService;
                var selector = selectorService.Parse(elementId);
                var condition = BuildCondition(selector);
                if (condition is not null)
                {
                    target = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
                }
            }

            if (target is not null)
            {
                startElement = target;
            }
        }

        var elements = new List<UiElement>();
        WalkTree(startElement, depth, 0, "", elements, ref nextElementId);

        // Promote unique AutomationIds to selectors (more stable than slugs)
        PromoteUniqueAutomationIds(root, elements);

        var result = elements.ToArray();
        return Task.FromResult(result);
    }

    public Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct)
    {
        _logger.LogDebug("Inspecting ancestors of {ElementId}", elementId);
        var nextElementId = 0;

        var root = GetRootElement(session);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        // Try as slug first, then legacy selector
        IUIAutomationElement? target = null;

        var slugParsed = SlugGenerator.ParseSlug(elementId);
        if (slugParsed is not null)
        {
            var slugResult = FindElementBySlug(elementId, root);
            if (slugResult is not null)
            {
                target = ResolveComElement(session, slugResult);
            }
        }
        else
        {
            var selector = _selectorService.Parse(elementId);
            var condition = BuildCondition(selector);
            if (condition is not null)
            {
                target = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            }
        }

        if (target is null)
        {
            throw new InvalidOperationException($"No element found matching '{elementId}'.");
        }

        // Walk up via TreeWalker
        var ancestors = new List<UiElement>();
        var walker = _automation.get_ControlViewWalker();
        var current = target;

        // Add the target element itself first
        ancestors.Add(ToUiElement(current, "", ref nextElementId));

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

            ancestors.Add(ToUiElement(parent, "", ref nextElementId));
            current = parent;
        }

        // Reverse so root is first, target is last
        ancestors.Reverse();

        // Promote unique AutomationIds to selectors (more stable than slugs)
        PromoteUniqueAutomationIds(root, ancestors);

        var result = ancestors.ToArray();
        return Task.FromResult(result);
    }

    public Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct)
    {
        _logger.LogDebug("Searching in process {Pid}", session.ProcessId);
        var nextElementId = 0;

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
            results[i] = ToUiElement(el, "", ref nextElementId);

            // For any non-invokable match, find the nearest invokable ancestor
            if (!IsInvokable(el))
            {
                var ancestor = FindInvokableAncestor(el, root);
                if (ancestor is not null)
                {
                    results[i].InvokableAncestor = ToUiElement(ancestor, "", ref nextElementId);
                }
            }
        }

        // Promote unique AutomationIds to selectors (more stable than slugs)
        PromoteUniqueAutomationIds(root, results);

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

        // Slug resolution: walk tree, regenerate slugs, match and validate hash
        if (selector.IsSlug)
        {
            return Task.FromResult(FindElementBySlug(selector.Slug!, root));
        }

        // Try exact AutomationId match first (fast, unambiguous — used when inspect promoted a unique AutomationId)
        if (selector.Query is not null)
        {
            var exactAidCondition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                ComVariant.Create(selector.Query));
            var exactMatch = root.FindFirst(TreeScope.TreeScope_Descendants, exactAidCondition);
            if (exactMatch is not null)
            {
                var nextId = 0;
                return Task.FromResult<UiElement?>(ToUiElement(exactMatch, "", ref nextId));
            }
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
            var listing = new System.Text.StringBuilder();
            listing.AppendLine($"Selector matched {matchCount} elements:");
            for (int i = 0; i < Math.Min(matchCount, 5); i++)
            {
                var m = found.GetElement(i);
                var mName = SafeGetBstr(() => m.get_CurrentName());
                var mType = GetControlTypeName(m.get_CurrentControlType());
                var mAutoId = SafeGetBstr(() => m.get_CurrentAutomationId());
                var mRect = m.get_CurrentBoundingRectangle();
                var nameStr = mName is not null ? $" \"{mName}\"" : "";
                var boundsStr = $" ({mRect.left},{mRect.top} {mRect.right - mRect.left}x{mRect.bottom - mRect.top})";
                // Generate slug for suggestion
                string slugSuggestion;
                try
                {
                    unsafe
                    {
                        var runtimeId = m.GetRuntimeId();
                        slugSuggestion = SlugGenerator.GenerateSlugFromSafeArray(mType, mAutoId, mName, runtimeId);
                    }
                }
                catch { slugSuggestion = $"{SlugGenerator.GetPrefix(mType)}[{i}]"; }
                listing.AppendLine($"  [{i}] {mType}{nameStr}{boundsStr}  -> {slugSuggestion}");
            }
            if (matchCount > 5)
            {
                listing.AppendLine($"  ... and {matchCount - 5} more");
            }
            listing.Append("Use a slug from 'inspect' to target a specific element.");
            throw new InvalidOperationException(listing.ToString());
        }

        var element = found.GetElement(0);
        var nextElementId = 0;
        var result = ToUiElement(element, "", ref nextElementId);

        // Surface invokable ancestor for non-invokable elements
        if (!IsInvokable(element))
        {
            var ancestor = FindInvokableAncestor(element, root);
            if (ancestor is not null)
            {
                result.InvokableAncestor = ToUiElement(ancestor, "", ref nextElementId);
            }
        }

        return Task.FromResult<UiElement?>(result);
    }

    public Task<Dictionary<string, object?>> GetPropertiesAsync(UiSessionInfo session, UiElement element, string? propertyName, CancellationToken ct)
    {
        // Basic properties from the UiElement model
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

        // Include Value from the element model (captured at inspect/search time)
        if (element.Value is not null)
        {
            props["Value"] = element.Value;
        }

        // Query the live COM element for additional properties
        var comElement = ResolveComElement(session, element);
        if (comElement is not null)
        {
            // General UIA properties (convert COM BOOL to C# bool)
            try { props["HasKeyboardFocus"] = (bool)comElement.get_CurrentHasKeyboardFocus(); } catch { }
            try { props["IsKeyboardFocusable"] = (bool)comElement.get_CurrentIsKeyboardFocusable(); } catch { }
            try { var v = SafeGetBstr(() => comElement.get_CurrentAcceleratorKey()); if (v is not null) { props["AcceleratorKey"] = v; } } catch { }
            try { var v = SafeGetBstr(() => comElement.get_CurrentAccessKey()); if (v is not null) { props["AccessKey"] = v; } } catch { }
            try { var v = SafeGetBstr(() => comElement.get_CurrentHelpText()); if (v is not null) { props["HelpText"] = v; } } catch { }
            try { props["IsPassword"] = comElement.get_CurrentIsContentElement() && comElement.get_CurrentControlType() == UIA_CONTROLTYPE_ID.UIA_EditControlTypeId; } catch { }

            // Pattern-specific properties
            try
            {
                var pattern = (IUIAutomationTogglePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
                props["ToggleState"] = pattern.get_CurrentToggleState() switch
                {
                    Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Off => "Off",
                    Windows.Win32.UI.Accessibility.ToggleState.ToggleState_On => "On",
                    Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Indeterminate => "Indeterminate",
                    _ => pattern.get_CurrentToggleState().ToString()
                };
            }
            catch { }

            try
            {
                var pattern = (IUIAutomationValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
                var v = pattern.get_CurrentValue();
                props["Value"] = v.ToString();
                props["IsReadOnly"] = (bool)pattern.get_CurrentIsReadOnly();
            }
            catch { }

            try
            {
                if (comElement is IUIAutomationSelectionItemPattern selPattern)
                {
                    props["IsSelected"] = (bool)selPattern.get_CurrentIsSelected();
                }
                else
                {
                    var pattern = (IUIAutomationSelectionItemPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionItemPatternId);
                    props["IsSelected"] = (bool)pattern.get_CurrentIsSelected();
                }
            }
            catch { }

            try
            {
                var pattern = (IUIAutomationExpandCollapsePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
                props["ExpandCollapseState"] = pattern.get_CurrentExpandCollapseState() switch
                {
                    Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Collapsed => "Collapsed",
                    Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Expanded => "Expanded",
                    Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_PartiallyExpanded => "PartiallyExpanded",
                    Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_LeafNode => "LeafNode",
                    _ => pattern.get_CurrentExpandCollapseState().ToString()
                };
            }
            catch { }

            try
            {
                var pattern = (IUIAutomationScrollPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
                props["ScrollHorizontalPercent"] = pattern.get_CurrentHorizontalScrollPercent();
                props["ScrollVerticalPercent"] = pattern.get_CurrentVerticalScrollPercent();
                props["HorizontallyScrollable"] = pattern.get_CurrentHorizontallyScrollable();
                props["VerticallyScrollable"] = pattern.get_CurrentVerticallyScrollable();
            }
            catch { }
        }

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

        var hint = element.InvokableAncestor is null
            ? "No invokable ancestor was found either — this element is display-only and cannot be activated."
            : $"Try the invokable ancestor: {element.InvokableAncestor.Selector ?? element.InvokableAncestor.Id}";

        throw new InvalidOperationException(
            $"Element {element.Selector ?? element.Id} ({element.Type}) does not support any invoke pattern. {hint}");
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
                var bstrPtr = Marshal.StringToBSTR(text);
                try
                {
                    pattern.SetValue(new Windows.Win32.Foundation.BSTR((char*)bstrPtr));
                }
                finally
                {
                    Marshal.FreeBSTR(bstrPtr);
                }
            }
            return Task.CompletedTask;
        }
        catch
        {
            // ValuePattern not supported — try RangeValuePattern for sliders/progress bars
            if (double.TryParse(text, out var numericValue))
            {
                try
                {
                    var rangePattern = (IUIAutomationRangeValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_RangeValuePatternId);
                    rangePattern.SetValue(numericValue);
                    return Task.CompletedTask;
                }
                catch { }
            }

            throw new InvalidOperationException(
                $"Element {element.Id} ({element.Type}) does not support ValuePattern or RangeValuePattern. " +
                "Only editable controls (TextBox, ComboBox, Slider, etc.) support set-value.");
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

    public Task<string?> GetTextAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        _logger.LogDebug("Getting text from element {ElementId}", element.Id);

        var comElement = ResolveComElement(session, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        // 1. Try TextPattern (RichEditBox, Document controls — full text with formatting support)
        try
        {
            var pattern = (IUIAutomationTextPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId);
            var range = pattern.get_DocumentRange();
            var text = range.GetText(-1);
            if (text.Length > 0)
            {
                return Task.FromResult<string?>(text.ToString());
            }
        }
        catch { }

        // 2. Try ValuePattern (TextBox, ComboBox — simple text)
        try
        {
            var pattern = (IUIAutomationValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
            var bstr = pattern.get_CurrentValue();
            var text = bstr.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                return Task.FromResult<string?>(text);
            }
        }
        catch { }

        // 3. Fall back to element Name (static text, labels)
        if (!string.IsNullOrEmpty(element.Name))
        {
            return Task.FromResult<string?>(element.Name);
        }

        return Task.FromResult<string?>(null);
    }

    public Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        _logger.LogDebug("Scrolling element {ElementId} into view", element.Id);

        var comElement = ResolveComElement(session, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        // Record position before scroll for verification
        var rectBefore = comElement.get_CurrentBoundingRectangle();

        try
        {
            var pattern = (IUIAutomationScrollItemPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollItemPatternId);
            pattern.ScrollIntoView();

            // Brief wait for scroll animation
            Thread.Sleep(100);

            // Verify position changed
            var rectAfter = comElement.get_CurrentBoundingRectangle();
            if (rectBefore.top == rectAfter.top && rectBefore.left == rectAfter.left)
            {
                _logger.LogWarning("Element position unchanged after ScrollIntoView — the element may already be visible or the container didn't respond.");
            }

            return Task.CompletedTask;
        }
        catch
        {
            // ScrollItemPattern not supported — try ScrollPattern on parent as fallback
            try
            {
                var walker = _automation.get_ControlViewWalker();
                var parent = walker.GetParentElement(comElement);
                var maxWalk = 5;
                while (parent is not null && maxWalk-- > 0)
                {
                    try
                    {
                        var scrollPattern = (IUIAutomationScrollPattern)parent.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
                        // Calculate how much to scroll — try to bring element into view
                        var elRect = comElement.get_CurrentBoundingRectangle();
                        var parentRect = parent.get_CurrentBoundingRectangle();

                        if (elRect.top < parentRect.top || elRect.bottom > parentRect.bottom)
                        {
                            scrollPattern.SetScrollPercent(-1, // horizontal: no change
                                Math.Max(0, Math.Min(100,
                                    scrollPattern.get_CurrentVerticalScrollPercent() +
                                    ((double)(elRect.top - parentRect.top) / (parentRect.bottom - parentRect.top) * 100))));
                        }

                        return Task.CompletedTask;
                    }
                    catch
                    {
                        parent = walker.GetParentElement(parent);
                    }
                }
            }
            catch { }

            throw new InvalidOperationException(
                $"Element {element.Id} ({element.Type}) does not support ScrollItemPattern and no scrollable ancestor found.");
        }
    }

    public Task ScrollContainerAsync(UiSessionInfo session, UiElement element, string? direction, string? to, CancellationToken ct)
    {
        _logger.LogDebug("Scrolling container {ElementId}", element.Id);

        var comElement = ResolveComElement(session, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        IUIAutomationScrollPattern? scrollPattern = null;
        try
        {
            scrollPattern = (IUIAutomationScrollPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
        }
        catch { }

        // If target doesn't support ScrollPattern, walk up to find a scrollable parent
        if (scrollPattern is null)
        {
            var walker = _automation.get_ControlViewWalker();
            var parent = walker.GetParentElement(comElement);
            var maxWalk = 10;
            while (parent is not null && maxWalk-- > 0)
            {
                try
                {
                    scrollPattern = (IUIAutomationScrollPattern)parent.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
                    break;
                }
                catch
                {
                    parent = walker.GetParentElement(parent);
                }
            }

            if (scrollPattern is null)
            {
                throw new InvalidOperationException(
                    $"Element {element.Selector ?? element.Id} ({element.Type}) and its ancestors do not support ScrollPattern.");
            }
        }

        if (to is not null)
        {
            var canScrollV = (bool)scrollPattern.get_CurrentVerticallyScrollable();
            if (!canScrollV)
            {
                throw new InvalidOperationException(
                    $"Element {element.Selector ?? element.Id} cannot scroll vertically (required for --to top/bottom).");
            }

            switch (to.ToLowerInvariant())
            {
                case "top":
                    scrollPattern.SetScrollPercent(-1, 0);
                    break;
                case "bottom":
                    scrollPattern.SetScrollPercent(-1, 100);
                    break;
                default:
                    throw new ArgumentException($"Invalid --to value '{to}'. Use 'top' or 'bottom'.");
            }
        }
        else if (direction is not null)
        {
            var currentV = scrollPattern.get_CurrentVerticalScrollPercent();
            var currentH = scrollPattern.get_CurrentHorizontalScrollPercent();
            var canScrollV = (bool)scrollPattern.get_CurrentVerticallyScrollable();
            var canScrollH = (bool)scrollPattern.get_CurrentHorizontallyScrollable();
            const double pageStep = 20.0;

            switch (direction.ToLowerInvariant())
            {
                case "down":
                    if (!canScrollV)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll vertically. " +
                            (canScrollH ? "It can scroll horizontally — try --direction right." : ""));
                    }
                    scrollPattern.SetScrollPercent(-1, Math.Min(100, currentV + pageStep));
                    break;
                case "up":
                    if (!canScrollV)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll vertically. " +
                            (canScrollH ? "It can scroll horizontally — try --direction left." : ""));
                    }
                    scrollPattern.SetScrollPercent(-1, Math.Max(0, currentV - pageStep));
                    break;
                case "right":
                    if (!canScrollH)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll horizontally. " +
                            (canScrollV ? "It can scroll vertically — try --direction down." : ""));
                    }
                    scrollPattern.SetScrollPercent(Math.Min(100, currentH + pageStep), -1);
                    break;
                case "left":
                    if (!canScrollH)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll horizontally. " +
                            (canScrollV ? "It can scroll vertically — try --direction up." : ""));
                    }
                    scrollPattern.SetScrollPercent(Math.Max(0, currentH - pageStep), -1);
                    break;
                default:
                    throw new ArgumentException($"Invalid --direction '{direction}'. Use up, down, left, or right.");
            }
        }

        return Task.CompletedTask;
    }

    public Task<UiElement?> GetFocusedElementAsync(UiSessionInfo session, CancellationToken ct)
    {
        _logger.LogDebug("Getting focused element for process {Pid}", session.ProcessId);

        IUIAutomationElement? focused;
        try
        {
            focused = _automation.GetFocusedElement();
        }
        catch
        {
            return Task.FromResult<UiElement?>(null);
        }

        if (focused is null)
        {
            return Task.FromResult<UiElement?>(null);
        }

        // Verify the focused element belongs to the target process
        try
        {
            var pid = focused.get_CurrentProcessId();
            if (pid != session.ProcessId)
            {
                return Task.FromResult<UiElement?>(null);
            }
        }
        catch
        {
            return Task.FromResult<UiElement?>(null);
        }

        var nextElementId = 0;
        var result = ToUiElement(focused, "", ref nextElementId);
        return Task.FromResult<UiElement?>(result);
    }

    /// <summary>
    /// Resolves a slug selector by walking the tree, regenerating slugs for each element,
    /// and matching + validating the RuntimeId hash.
    /// Returns both the UiElement model and the live COM element.
    /// </summary>
    private (UiElement? Model, IUIAutomationElement? ComElement) FindElementBySlugWithCom(string targetSlug, IUIAutomationElement root)
    {
        var parsed = SlugGenerator.ParseSlug(targetSlug);
        if (parsed is null)
        {
            return (null, null);
        }

        var (targetPrefix, targetNameSlug, targetHash) = parsed.Value;
        var nextElementId = 0;
        const int maxRecursionDepth = 100;

        // DFS walk to find matching slug
        IUIAutomationElement? matchedCom = null;
        UiElement? matchedUi = null;
        bool hashMismatchFound = false;

        void Walk(IUIAutomationElement element, int depth)
        {
            if (matchedCom is not null || depth > maxRecursionDepth)
            {
                return;
            }

            var type = GetControlTypeName(element.get_CurrentControlType());
            var name = SafeGetBstr(() => element.get_CurrentName());
            var automationId = SafeGetBstr(() => element.get_CurrentAutomationId());

            var prefix = SlugGenerator.GetPrefix(type);
            var nameSlug = SlugGenerator.Normalize(automationId) ?? SlugGenerator.Normalize(name);

            // Check prefix and name match
            if (prefix == targetPrefix && nameSlug == targetNameSlug)
            {
                // Validate RuntimeId hash
                try
                {
                    unsafe
                    {
                        var runtimeId = element.GetRuntimeId();
                        var hash = SlugGenerator.ComputeHashFromSafeArray(runtimeId);
                        if (hash == targetHash)
                        {
                            matchedCom = element;
                            matchedUi = ToUiElement(element, "", ref nextElementId);
                            return;
                        }
                        else
                        {
                            hashMismatchFound = true;
                        }
                    }
                }
                catch { }
            }

            // Also handle nameless elements: prefix-hash (no name slug)
            if (targetNameSlug is null && prefix == targetPrefix)
            {
                try
                {
                    unsafe
                    {
                        var runtimeId = element.GetRuntimeId();
                        var hash = SlugGenerator.ComputeHashFromSafeArray(runtimeId);
                        if (hash == targetHash)
                        {
                            matchedCom = element;
                            matchedUi = ToUiElement(element, "", ref nextElementId);
                            return;
                        }
                    }
                }
                catch { }
            }

            // Recurse children
            var walker = _automation.get_ControlViewWalker();
            var child = walker.GetFirstChildElement(element);
            while (child is not null && matchedCom is null)
            {
                Walk(child, depth + 1);
                try { child = walker.GetNextSiblingElement(child); }
                catch { break; }
            }
        }

        Walk(root, 0);

        if (matchedUi is not null)
        {
            // Surface invokable ancestor
            if (matchedCom is not null && !IsInvokable(matchedCom))
            {
                var ancestor = FindInvokableAncestor(matchedCom, root);
                if (ancestor is not null)
                {
                    matchedUi.InvokableAncestor = ToUiElement(ancestor, "", ref nextElementId);
                }
            }
            return (matchedUi, matchedCom);
        }

        if (hashMismatchFound)
        {
            throw new InvalidOperationException(
                $"Element with slug '{targetSlug}' found by name but RuntimeId hash doesn't match — " +
                "the UI may have changed. Re-run 'inspect' to get updated selectors.");
        }

        return (null, null);
    }

    /// <summary>
    /// Convenience wrapper that returns only the UiElement model from slug resolution.
    /// </summary>
    private UiElement? FindElementBySlug(string targetSlug, IUIAutomationElement root)
    {
        return FindElementBySlugWithCom(targetSlug, root).Model;
    }

    // --- Private helpers ---

    /// <summary>
    /// Re-finds a live COM UIA element from our serialized UiElement model.
    /// Uses slug-based resolution first (most precise), then falls back to
    /// AutomationId or Name+Type property matching.
    /// </summary>
    private IUIAutomationElement? ResolveComElement(UiSessionInfo session, UiElement element)
    {
        var root = GetRootElement(session);
        if (root is null)
        {
            return null;
        }

        // Try slug-based resolution first (most precise — uses RuntimeId hash)
        if (element.Selector is not null)
        {
            var (_, comElement) = FindElementBySlugWithCom(element.Selector, root);
            if (comElement is not null)
            {
                return comElement;
            }
        }

        // Fall back to AutomationId (stable but not unique across duplicates)
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

        // Fall back to Name + ControlType
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

            // Multiple top-level elements — try matching by window title
            var titleQuery = session.WindowTitle;
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
        if (selector.Query is not null)
        {
            // Plain text search: substring match on Name OR AutomationId (case-insensitive)
            var nameCondition = _automation.CreatePropertyConditionEx(
                UIA_PROPERTY_ID.UIA_NamePropertyId,
                ComVariant.Create(selector.Query),
                PropertyConditionFlags.PropertyConditionFlags_MatchSubstring | PropertyConditionFlags.PropertyConditionFlags_IgnoreCase);

            var autoIdCondition = _automation.CreatePropertyConditionEx(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                ComVariant.Create(selector.Query),
                PropertyConditionFlags.PropertyConditionFlags_MatchSubstring | PropertyConditionFlags.PropertyConditionFlags_IgnoreCase);

            return _automation.CreateOrCondition(nameCondition, autoIdCondition);
        }

        return null;
    }

    /// <summary>
    /// Checks if an element supports any invokable pattern (Invoke, Toggle, SelectionItem, ExpandCollapse).
    /// </summary>
    private static bool IsInvokable(IUIAutomationElement element)
    {
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_InvokePatternId);
            if (obj is IUIAutomationInvokePattern) { return true; }
        }
        catch { }
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
            if (obj is IUIAutomationTogglePattern) { return true; }
        }
        catch { }
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionItemPatternId);
            if (obj is IUIAutomationSelectionItemPattern) { return true; }
        }
        catch { }
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
            if (obj is IUIAutomationExpandCollapsePattern) { return true; }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Walks up the tree from an element to find the nearest ancestor that supports an invoke pattern.
    /// Stops at the root element to avoid walking past the target window.
    /// </summary>
    private IUIAutomationElement? FindInvokableAncestor(IUIAutomationElement element, IUIAutomationElement root)
    {
        var walker = _automation.get_ControlViewWalker();
        var current = walker.GetParentElement(element);
        var maxDepth = 10; // prevent runaway walks

        while (current is not null && maxDepth-- > 0)
        {
            // Stop at the root window
            try
            {
                if (_automation.CompareElements(current, root))
                {
                    break;
                }
            }
            catch
            {
                break;
            }

            if (IsInvokable(current))
            {
                return current;
            }

            try
            {
                current = walker.GetParentElement(current);
            }
            catch
            {
                break;
            }
        }

        return null;
    }

    private void WalkTree(IUIAutomationElement element, int maxDepth, int currentDepth, string path, List<UiElement> results, ref int nextElementId)
    {
        var uiElement = ToUiElement(element, path, ref nextElementId);
        uiElement.Depth = currentDepth;
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
            WalkTree(child, maxDepth, currentDepth + 1, childPath, results, ref nextElementId);

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

    private static UiElement ToUiElement(IUIAutomationElement element, string path, ref int nextElementId)
    {
        var id = $"e{nextElementId++}";
        var rect = element.get_CurrentBoundingRectangle();
        var type = GetControlTypeName(element.get_CurrentControlType());
        var name = SafeGetBstr(() => element.get_CurrentName());
        var automationId = SafeGetBstr(() => element.get_CurrentAutomationId());

        // Try to get current value for editable elements (TextBox, ComboBox, etc.)
        string? value = null;
        try
        {
            var valuePattern = (IUIAutomationValuePattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
            var bstr = valuePattern.get_CurrentValue();
            var v = bstr.ToString();
            if (!string.IsNullOrEmpty(v))
            {
                value = v;
            }
        }
        catch { }

        // Try to get toggle state for checkboxes/toggles
        string? toggleState = null;
        try
        {
            var pattern = (IUIAutomationTogglePattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
            toggleState = pattern.get_CurrentToggleState() switch
            {
                Windows.Win32.UI.Accessibility.ToggleState.ToggleState_On => "on",
                Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Off => "off",
                Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Indeterminate => "indeterminate",
                _ => null
            };
        }
        catch { }

        // Try to get expand/collapse state
        string? expandState = null;
        try
        {
            var pattern = (IUIAutomationExpandCollapsePattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
            var state = pattern.get_CurrentExpandCollapseState();
            if (state != Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_LeafNode)
            {
                expandState = state switch
                {
                    Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Expanded => "expanded",
                    Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Collapsed => "collapsed",
                    _ => null
                };
            }
        }
        catch { }

        // Generate semantic slug from RuntimeId
        string? selector = null;
        try
        {
            unsafe
            {
                var runtimeId = element.GetRuntimeId();
                selector = SlugGenerator.GenerateSlugFromSafeArray(type, automationId, name, runtimeId);
            }
        }
        catch { }

        // Check scroll capability
        string? scrollDir = null;
        try
        {
            var sp = (IUIAutomationScrollPattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
            var v = (bool)sp.get_CurrentVerticallyScrollable();
            var h = (bool)sp.get_CurrentHorizontallyScrollable();
            if (v && h) { scrollDir = "vh"; }
            else if (v) { scrollDir = "v"; }
            else if (h) { scrollDir = "h"; }
        }
        catch { }

        return new UiElement
        {
            Id = id,
            Type = type,
            Name = name,
            AutomationId = automationId,
            ClassName = SafeGetBstr(() => element.get_CurrentClassName()),
            IsEnabled = element.get_CurrentIsEnabled(),
            IsOffscreen = element.get_CurrentIsOffscreen(),
            X = rect.left,
            Y = rect.top,
            Width = rect.right - rect.left,
            Height = rect.bottom - rect.top,
            Value = value,
            ToggleState = toggleState,
            ExpandState = expandState,
            ScrollDir = scrollDir,
            Selector = selector,
        };
    }

    /// <summary>
    /// Promotes unique AutomationIds to selectors. When an element's AutomationId is unique
    /// across the full UIA tree, use it directly as the selector instead of a generated slug.
    /// AutomationIds are developer-set, stable across layout changes, and more readable.
    /// </summary>
    private void PromoteUniqueAutomationIds(IUIAutomationElement root, IList<UiElement> elements)
    {
        // Collect AutomationIds from the inspected elements that could be promoted
        var candidateAids = new HashSet<string>();
        foreach (var el in elements)
        {
            if (el.AutomationId is not null)
            {
                candidateAids.Add(el.AutomationId);
            }
        }

        if (candidateAids.Count == 0)
        {
            return;
        }

        // Build frequency map from the FULL tree to check global uniqueness
        var aidCounts = new Dictionary<string, int>();
        try
        {
            var allElements = root.FindAll(
                TreeScope.TreeScope_Descendants,
                _automation.CreateTrueCondition());

            if (allElements is not null)
            {
                var count = allElements.get_Length();
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var aid = SafeGetBstr(() => allElements.GetElement(i).get_CurrentAutomationId());
                        if (aid is not null && candidateAids.Contains(aid))
                        {
                            aidCounts[aid] = aidCounts.GetValueOrDefault(aid) + 1;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("AutomationId uniqueness check failed: {Message}", ex.Message);
            return;
        }

        // Promote elements with globally unique AutomationIds
        foreach (var el in elements)
        {
            if (el.AutomationId is not null &&
                aidCounts.TryGetValue(el.AutomationId, out var count) && count == 1)
            {
                el.Selector = el.AutomationId;
            }
        }
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

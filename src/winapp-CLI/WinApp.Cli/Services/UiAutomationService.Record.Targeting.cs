// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class UiAutomationService
{
    /// <summary>Retargets capture to an element's popup or owned top-level window.</summary>
    internal static nint ResolvePopupCaptureHwnd(
        long? elementWindowHandle,
        nint sessionHwnd,
        ref int captureOriginLeft,
        ref int captureOriginTop,
        ref int srcWidth,
        ref int srcHeight,
        Func<nint, nint>? getAncestorRoot = null,
        Func<nint, (int left, int top, int right, int bottom)>? getWindowRect = null)
    {
        if (!elementWindowHandle.HasValue || elementWindowHandle.Value == sessionHwnd)
        {
            return sessionHwnd;
        }

        var rawElementHwnd = (nint)elementWindowHandle.Value;
        nint elementOwnerHwnd;
        if (getAncestorRoot is not null)
        {
            var root = getAncestorRoot(rawElementHwnd);
            elementOwnerHwnd = root != 0 ? root : rawElementHwnd;
        }
        else
        {
            var rootHwnd = Windows.Win32.PInvoke.GetAncestor(
                new Windows.Win32.Foundation.HWND(rawElementHwnd),
                Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
            elementOwnerHwnd = rootHwnd.IsNull ? rawElementHwnd : (nint)rootHwnd;
        }

        if (elementOwnerHwnd == sessionHwnd)
        {
            return sessionHwnd;
        }

        if (getWindowRect is not null)
        {
            var (left, top, right, bottom) = getWindowRect(elementOwnerHwnd);
            captureOriginLeft = left;
            captureOriginTop = top;
            srcWidth = Math.Max(1, right - left);
            srcHeight = Math.Max(1, bottom - top);
        }
        else
        {
            Windows.Win32.PInvoke.GetWindowRect(
                new Windows.Win32.Foundation.HWND(elementOwnerHwnd),
                out var popupRect);
            captureOriginLeft = popupRect.left;
            captureOriginTop = popupRect.top;
            srcWidth = Math.Max(1, popupRect.right - popupRect.left);
            srcHeight = Math.Max(1, popupRect.bottom - popupRect.top);
        }

        return elementOwnerHwnd;
    }

    internal static bool IsElementOffscreen(
        double elementX,
        double elementY,
        double elementWidth,
        double elementHeight,
        int captureOriginLeft,
        int captureOriginTop,
        int sourceWidth,
        int sourceHeight)
    {
        if (elementWidth <= 0 || elementHeight <= 0)
        {
            return true;
        }

        var intersectionLeft = Math.Max((int)elementX, captureOriginLeft);
        var intersectionTop = Math.Max((int)elementY, captureOriginTop);
        var intersectionRight = Math.Min((int)elementX + (int)elementWidth, captureOriginLeft + sourceWidth);
        var intersectionBottom = Math.Min((int)elementY + (int)elementHeight, captureOriginTop + sourceHeight);
        return intersectionRight <= intersectionLeft || intersectionBottom <= intersectionTop;
    }

    /// <summary>Resolves the element's top-level native window through its UIA ancestors.</summary>
    private nint ResolveElementTopLevelHwnd(UiSessionInfo session, UiElement selectorElement)
    {
        try
        {
            var comElement = ResolveComElement(session, selectorElement);
            if (comElement is null)
            {
                return 0;
            }

            var walker = _automation.get_ControlViewWalker();
            var current = comElement;
            var maxWalk = 40;
            while (current is not null && maxWalk-- > 0)
            {
                var native = current.get_CurrentNativeWindowHandle();
                if (!native.IsNull)
                {
                    var root = Windows.Win32.PInvoke.GetAncestor(
                        native,
                        Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
                    return root.IsNull ? (nint)native : (nint)root;
                }
                current = walker.GetParentElement(current);
            }

            return 0;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogDebug(ex, "Deriving element top-level HWND failed; leaving capture on the session window");
            return 0;
        }
    }

    /// <summary>Retargets capture to a resolved element window.</summary>
    internal static nint DeriveElementCaptureHwnd(
        nint sessionHwnd,
        ref int captureOriginLeft,
        ref int captureOriginTop,
        ref int srcWidth,
        ref int srcHeight,
        Func<nint> getElementTopLevelHwnd,
        Func<nint, (int left, int top, int right, int bottom)>? getWindowRect = null)
    {
        var derived = getElementTopLevelHwnd();
        if (derived == 0 || derived == sessionHwnd)
        {
            return sessionHwnd;
        }

        if (getWindowRect is not null)
        {
            var (left, top, right, bottom) = getWindowRect(derived);
            captureOriginLeft = left;
            captureOriginTop = top;
            srcWidth = Math.Max(1, right - left);
            srcHeight = Math.Max(1, bottom - top);
        }
        else
        {
            Windows.Win32.PInvoke.GetWindowRect(
                new Windows.Win32.Foundation.HWND(derived),
                out var derivedRect);
            captureOriginLeft = derivedRect.left;
            captureOriginTop = derivedRect.top;
            srcWidth = Math.Max(1, derivedRect.right - derivedRect.left);
            srcHeight = Math.Max(1, derivedRect.bottom - derivedRect.top);
        }

        return derived;
    }
}

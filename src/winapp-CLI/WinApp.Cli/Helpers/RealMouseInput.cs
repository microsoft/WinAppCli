// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Production implementation — delegates to <see cref="MouseInput"/> static P/Invoke helpers.
/// </summary>
internal class RealMouseInput : IMouseInput
{
    public void Hover(int screenX, int screenY) => MouseInput.Hover(screenX, screenY);

    public void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false)
        => MouseInput.Click(screenX, screenY, doubleClick, rightClick);

    public void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false)
        => MouseInput.Drag(fromScreenX, fromScreenY, toScreenX, toScreenY, rightButton);

    public void ScrollWheel(int screenX, int screenY, int delta)
        => MouseInput.ScrollWheel(screenX, screenY, delta);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Abstraction over the pre-injection foreground check for testability. The real implementation
/// performs live <c>GetForegroundWindow</c> P/Invoke (see <see cref="ForegroundGuard"/>); fakes can
/// force the proceed/abort decision so coordinate-gesture verbs (click, hover, drag, scroll --wheel,
/// send-keys --via send-input) can be unit-tested without a live, unlocked desktop.
/// </summary>
internal interface IForegroundGuard
{
    /// <summary>
    /// Verifies the target window is in the foreground before an OS-wide input injection and emits the
    /// appropriate error when it isn't. Returns <see langword="true"/> to proceed, <see langword="false"/>
    /// to abort. See <see cref="ForegroundGuard.TryEnsureForeground"/> for the production semantics.
    /// </summary>
    /// <param name="action">Verb used in the message, e.g. "click", "drag", "scroll --wheel".</param>
    bool TryEnsureForeground(long targetHwnd, ILogger logger, bool json, string action);
}

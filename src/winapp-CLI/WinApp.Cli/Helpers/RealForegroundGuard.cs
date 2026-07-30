// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Production implementation — delegates to the <see cref="ForegroundGuard"/> static helper so the
/// well-tested foreground-classification logic stays the single source of truth.
/// </summary>
internal class RealForegroundGuard : IForegroundGuard
{
    public bool TryEnsureForeground(long targetHwnd, ILogger logger, bool json, string action)
        => ForegroundGuard.TryEnsureForeground(targetHwnd, logger, json, action);

    public bool IsRemoteSession() => ForegroundGuard.IsRemoteSession();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IUpdateNotificationService
{
    /// <summary>
    /// Shows a cached update notice (if available and not yet shown today) with zero network I/O.
    /// If the cache is stale (&gt;24 h), a background refresh is started (fire-and-forget).
    /// This method is synchronous and never blocks on the network.
    /// </summary>
    void CheckAndNotify();
}

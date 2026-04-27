// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IUpdateNotificationService
{
    /// <summary>
    /// Checks if an update is available (at most once per day) and prints a notification.
    /// Failures are silently ignored; cancellation is allowed to propagate.
    /// </summary>
    Task CheckAndNotifyAsync(CancellationToken cancellationToken = default);
}

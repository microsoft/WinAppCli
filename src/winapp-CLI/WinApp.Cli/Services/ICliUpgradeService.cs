// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal enum InstallChannel
{
    Msix,
    StandaloneExe,
    Npm,
    NuGet
}

internal interface ICliUpgradeService
{
    /// <summary>
    /// Detects how the CLI was installed (MSIX, standalone exe, npm, NuGet).
    /// </summary>
    InstallChannel DetectInstallChannel();

    /// <summary>
    /// Checks GitHub for the latest release version. Returns null on failure.
    /// </summary>
    Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an update is available (at most once per day) and prints a notification.
    /// Failures are silently ignored.
    /// </summary>
    Task CheckAndNotifyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the upgrade based on the detected install channel.
    /// Skips the upgrade if the current version is already up to date (unless force is true).
    /// Returns 0 on success, non-zero on failure.
    /// </summary>
    Task<int> UpgradeAsync(bool force = false, CancellationToken cancellationToken = default);
}

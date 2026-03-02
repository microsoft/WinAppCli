// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Services;

internal interface IAppLauncherService
{
    uint LaunchByAumid(string aumid, string? arguments = null);
    string ComputePackageFamilyName(string packageName, string publisher);

    /// <summary>
    /// Launches a packaged app via its execution alias, with stdio redirection.
    /// Returns the Process object so the caller can read stdout/stderr.
    /// </summary>
    Process LaunchByAlias(string aliasName, string? arguments = null);

    /// <summary>
    /// Enables debugging mode for a package using IPackageDebugSettings.
    /// Disables PLM (Process Lifecycle Management) suspension so the app isn't
    /// suspended when it loses focus during development.
    /// </summary>
    void EnablePackageDebugging(string packageFullName);

    /// <summary>
    /// Disables debugging mode for a package, re-enabling normal PLM behavior.
    /// </summary>
    void DisablePackageDebugging(string packageFullName);
}

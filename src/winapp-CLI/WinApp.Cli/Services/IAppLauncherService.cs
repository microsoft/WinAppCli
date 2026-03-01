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
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IAppLauncherService
{
    uint LaunchByAumid(string aumid, string? arguments = null);
    string ComputePackageFamilyName(string packageName, string publisher);
}

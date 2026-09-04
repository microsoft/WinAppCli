// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IWindowsNativeToolchainResolver
{
    Task<WindowsNativeToolchainResolution> ResolveAsync(
        WindowsNativeToolchainRequirements requirements,
        CancellationToken cancellationToken);
}

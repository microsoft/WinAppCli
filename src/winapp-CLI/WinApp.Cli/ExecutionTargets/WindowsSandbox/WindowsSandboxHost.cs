// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal interface IWindowsSandboxHost
{
    bool IsSupportedOperatingSystem { get; }
}

internal sealed class WindowsSandboxHost : IWindowsSandboxHost
{
    public bool IsSupportedOperatingSystem =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);
}

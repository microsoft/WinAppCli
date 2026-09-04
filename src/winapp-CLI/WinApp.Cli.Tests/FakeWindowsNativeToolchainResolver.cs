// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

internal sealed class FakeWindowsNativeToolchainResolver : IWindowsNativeToolchainResolver
{
    public WindowsNativeToolchainResolution Result { get; set; } = new(
        new WindowsNativeToolchain(
            @"C:\VS",
            "18.8.0",
            "14.51.0",
            null,
            @"C:\VS\link.exe",
            "10.0.26100.0",
            new Dictionary<string, string> { ["PATH"] = @"C:\VS" }));

    public List<WindowsNativeToolchainRequirements> Calls { get; } = [];

    public Task<WindowsNativeToolchainResolution> ResolveAsync(
        WindowsNativeToolchainRequirements requirements,
        CancellationToken cancellationToken)
    {
        Calls.Add(requirements);
        return Task.FromResult(Result);
    }
}

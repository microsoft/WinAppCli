// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal sealed record WindowsAppRuntimePackageIdentity(string Name, string Version);

internal sealed class WindowsAppRuntimePrepareResult
{
    public required string DeploymentMode { get; init; }
    public required string Version { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string Architecture { get; init; }
    public required string OutputPath { get; init; }
    public required string BootstrapDllPath { get; init; }
    public required bool Ready { get; init; }
    public required bool RuntimeRegistered { get; init; }
    public required bool Installed { get; init; }
    public required int InstalledPackageCount { get; init; }
    public required IReadOnlyList<WindowsAppRuntimePackageIdentity> RuntimePackages { get; init; }
    public string? Guidance { get; init; }
}

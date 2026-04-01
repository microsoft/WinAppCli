// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake package registration service that records calls without actually
/// registering, unregistering, or installing MSIX packages.
/// </summary>
internal class FakePackageRegistrationService : IPackageRegistrationService
{
    public List<string> RegisterLooseLayoutCalls { get; } = [];
    public List<(string ManifestPath, string ExternalLocation)> RegisterSparseCalls { get; } = [];
    public List<string> UnregisterCalls { get; } = [];
    public List<string> InstallPackageCalls { get; } = [];
    public List<string> GetInstalledVersionCalls { get; } = [];
    public List<string> FindDevPackagesCalls { get; } = [];

    /// <summary>
    /// When set, <see cref="UnregisterAsync"/> returns this value.
    /// Defaults to false (no package found).
    /// </summary>
    public bool FakeUnregisterResult { get; set; }

    /// <summary>
    /// When set, <see cref="GetInstalledVersion"/> returns this value.
    /// Defaults to null (package not installed).
    /// </summary>
    public string? FakeInstalledVersion { get; set; }

    /// <summary>
    /// When set, <see cref="FindDevPackages"/> returns these values.
    /// Defaults to empty list.
    /// </summary>
    public List<DevPackageInfo> FakeDevPackages { get; set; } = [];

    public Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        RegisterLooseLayoutCalls.Add(manifestPath);
        return Task.CompletedTask;
    }

    public Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default)
    {
        RegisterSparseCalls.Add((manifestPath, externalLocation));
        return Task.CompletedTask;
    }

    public Task<bool> UnregisterAsync(string packageName, CancellationToken cancellationToken = default)
    {
        UnregisterCalls.Add(packageName);
        return Task.FromResult(FakeUnregisterResult);
    }

    public Task InstallPackageAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        InstallPackageCalls.Add(packagePath);
        return Task.CompletedTask;
    }

    public string? GetInstalledVersion(string packageName)
    {
        GetInstalledVersionCalls.Add(packageName);
        return FakeInstalledVersion;
    }

    public List<DevPackageInfo> FindDevPackages(string packageName)
    {
        FindDevPackagesCalls.Add(packageName);
        return FakeDevPackages;
    }
}

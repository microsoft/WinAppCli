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
    public List<(string PackageName, bool PreserveAppData)> UnregisterCalls { get; } = [];
    public List<(string PackageFullName, bool PreserveAppData)> UnregisterByFullNameCalls { get; } = [];
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
        if (RegisterLooseLayoutThrows is not null)
        {
            throw RegisterLooseLayoutThrows;
        }
        RegisterLooseLayoutCalls.Add(manifestPath);
        return Task.CompletedTask;
    }

    public Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default)
    {
        if (RegisterSparseThrows is not null)
        {
            throw RegisterSparseThrows;
        }
        RegisterSparseCalls.Add((manifestPath, externalLocation));
        return Task.CompletedTask;
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="RegisterLooseLayoutAsync"/> throws it.
    /// Used to exercise the exception-wrapping path in
    /// <c>MsixService.RegisterLooseLayoutPackageAsync</c>.
    /// </summary>
    public Exception? RegisterLooseLayoutThrows { get; set; }

    /// <summary>
    /// When set to a non-null exception, <see cref="RegisterSparseAsync"/> throws it.
    /// Used to exercise the exception-wrapping path in
    /// <c>MsixService.RegisterSparsePackageAsync</c>.
    /// </summary>
    public Exception? RegisterSparseThrows { get; set; }

    public Task<bool> UnregisterAsync(string packageName, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        UnregisterCalls.Add((packageName, preserveAppData));
        return Task.FromResult(FakeUnregisterResult);
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="UnregisterByFullNameAsync"/> throws it
    /// instead of recording the call. Useful for testing exception-propagation paths
    /// (e.g. cancellation surfacing from MsixService.UnregisterExistingPackageAsync).
    /// </summary>
    public Exception? UnregisterByFullNameThrows { get; set; }

    public Task UnregisterByFullNameAsync(string packageFullName, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        if (UnregisterByFullNameThrows is not null)
        {
            throw UnregisterByFullNameThrows;
        }
        UnregisterByFullNameCalls.Add((packageFullName, preserveAppData));
        return Task.CompletedTask;
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="InstallPackageAsync"/> throws it
    /// (after recording the call) instead of completing. Used to exercise the per-package
    /// install-failure/error-count path in
    /// <c>WorkspaceSetupService.InstallWindowsAppRuntimeAsync</c>.
    /// </summary>
    public Exception? InstallPackageThrows { get; set; }

    public Task InstallPackageAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        InstallPackageCalls.Add(packagePath);
        if (InstallPackageThrows is not null)
        {
            throw InstallPackageThrows;
        }
        return Task.CompletedTask;
    }

    public string? GetInstalledVersion(string packageName)
    {
        GetInstalledVersionCalls.Add(packageName);
        return FakeInstalledVersion;
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="FindDevPackages"/> throws it
    /// instead of returning <see cref="FakeDevPackages"/>. Use to exercise the
    /// non-fatal catch path (and OperationCanceled propagation) in
    /// <c>MsixService.IsExistingRegistrationUpToDate</c>.
    /// </summary>
    public Exception? FindDevPackagesThrows { get; set; }

    public List<DevPackageInfo> FindDevPackages(string packageName)
    {
        FindDevPackagesCalls.Add(packageName);
        if (FindDevPackagesThrows is not null)
        {
            throw FindDevPackagesThrows;
        }
        return FakeDevPackages;
    }
}

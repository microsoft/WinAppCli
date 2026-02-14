// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake DotNetService that delegates file-based operations to the real DotNetService
/// but fakes CLI-based operations (dotnet add package, dotnet list, etc.).
/// Tracks which packages were added for test assertions.
/// </summary>
internal class FakeDotNetService : IDotNetService
{
    private readonly DotNetService _real = new();

    /// <summary>
    /// Tracks packages added via AddOrUpdatePackageReferenceAsync
    /// </summary>
    public List<(string CsprojPath, string PackageName, string Version)> AddedPackages { get; } = [];

    /// <summary>
    /// Set this to control what GetPackageListAsync returns.
    /// When null, the method returns null (default behavior).
    /// </summary>
    public DotNetPackageListJson? PackageListResult { get; set; }

    // Delegate file-based operations to real implementation
    public IReadOnlyList<FileInfo> FindCsproj(DirectoryInfo directory) => _real.FindCsproj(directory);
    public string? GetTargetFramework(FileInfo csprojPath) => _real.GetTargetFramework(csprojPath);
    public bool IsMultiTargeted(FileInfo csprojPath) => _real.IsMultiTargeted(csprojPath);
    public bool IsTargetFrameworkSupported(string targetFramework) => _real.IsTargetFrameworkSupported(targetFramework);
    public string GetRecommendedTargetFramework(string? currentTargetFramework = null) => _real.GetRecommendedTargetFramework(currentTargetFramework);
    public void SetTargetFramework(FileInfo csprojPath, string newTargetFramework) => _real.SetTargetFramework(csprojPath, newTargetFramework);

    // Fake CLI-based operations
    public Task AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string version, CancellationToken cancellationToken = default)
    {
        AddedPackages.Add((csprojPath.FullName, packageName, version));
        return Task.CompletedTask;
    }

    public Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(DirectoryInfo workingDirectory, string arguments, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((0, "Fake dotnet command executed successfully.", string.Empty));
    }

    public Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo csprojFile, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PackageListResult);
    }
}

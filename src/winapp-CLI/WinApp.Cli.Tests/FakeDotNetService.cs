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
    public List<(string CsprojPath, string PackageName, string? Version)> AddedPackages { get; } = [];

    /// <summary>
    /// Set this to control what GetPackageListAsync returns.
    /// When null, the method returns null (default behavior).
    /// </summary>
    public DotNetPackageListJson? PackageListResult { get; set; }

    /// <summary>
    /// When true, <see cref="GetPackageListAsync"/> throws, simulating a failure of
    /// <c>dotnet list package --format json</c> (e.g. a project that fails to restore).
    /// </summary>
    public bool ThrowOnGetPackageList { get; set; }

    /// <summary>
    /// Number of times <see cref="GetPackageListAsync"/> has been invoked.
    /// </summary>
    public int GetPackageListCallCount { get; private set; }

    /// <summary>
    /// Packages listed here will cause <see cref="AddOrUpdatePackageReferenceAsync"/> to throw,
    /// simulating failures from <c>dotnet add package</c>.
    /// </summary>
    public HashSet<string> PackagesToThrowOnAdd { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Delegate file-based operations to real implementation
    public IReadOnlyList<FileInfo> FindCsproj(DirectoryInfo directory) => _real.FindCsproj(directory);
    public string? GetTargetFramework(FileInfo csprojPath) => _real.GetTargetFramework(csprojPath);
    public bool IsMultiTargeted(FileInfo csprojPath) => _real.IsMultiTargeted(csprojPath);
    public bool IsTargetFrameworkSupported(string targetFramework) => _real.IsTargetFrameworkSupported(targetFramework);
    public string GetRecommendedTargetFramework(string? currentTargetFramework = null) => _real.GetRecommendedTargetFramework(currentTargetFramework);
    public void SetTargetFramework(FileInfo csprojPath, string newTargetFramework) => _real.SetTargetFramework(csprojPath, newTargetFramework);
    public Task<bool> UpdatePublishProfileAsync(FileInfo csprojPath, CancellationToken cancellationToken = default) => _real.UpdatePublishProfileAsync(csprojPath, cancellationToken);
    public Task<bool> EnsureRuntimeIdentifierAsync(FileInfo csprojPath, CancellationToken cancellationToken = default) => _real.EnsureRuntimeIdentifierAsync(csprojPath, cancellationToken);
    public Task<bool> HasPackageReferenceAsync(FileInfo csprojPath, string packageName, CancellationToken cancellationToken = default)
    {
        if (PackageListResult?.Projects is null)
        {
            return Task.FromResult(false);
        }

        var found = PackageListResult.Projects
            .SelectMany(p => p.Frameworks ?? [])
            .SelectMany(f => f.TopLevelPackages ?? [])
            .Any(pkg => string.Equals(pkg.Id, packageName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(found);
    }

    // Fake CLI-based operations
    public Task<string> AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string? version, CancellationToken cancellationToken = default)
    {
        if (PackagesToThrowOnAdd.Contains(packageName))
        {
            throw new InvalidOperationException($"Simulated dotnet add package failure for {packageName}");
        }

        AddedPackages.Add((csprojPath.FullName, packageName, version));
        return Task.FromResult(version ?? "1.0.0");
    }

    public Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(DirectoryInfo workingDirectory, string arguments, CancellationToken cancellationToken = default)
    {
        StringInvocations.Add(arguments);
        return Task.FromResult((0, "Fake dotnet command executed successfully.", string.Empty));
    }

    /// <summary>Records every argument list passed to the <see cref="IReadOnlyList{T}"/> overload.</summary>
    public List<IReadOnlyList<string>> ArgumentListInvocations { get; } = [];

    /// <summary>Records every string passed to the string overload.</summary>
    public List<string> StringInvocations { get; } = [];

    /// <summary>
    /// Optional scripted responder for the <see cref="IReadOnlyList{T}"/> overload. When null, the
    /// call is recorded and a success tuple is returned. May throw to simulate a missing executable.
    /// </summary>
    public Func<IReadOnlyList<string>, (int ExitCode, string Output, string Error)>? RunDotnetArgumentListHandler { get; set; }

    /// <summary>Records the environment overrides passed alongside each argument-list invocation.</summary>
    public List<IReadOnlyDictionary<string, string>?> ArgumentListEnvironmentInvocations { get; } = [];

    public Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(DirectoryInfo workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environmentOverrides = null, CancellationToken cancellationToken = default)
    {
        ArgumentListInvocations.Add(arguments.ToArray());
        ArgumentListEnvironmentInvocations.Add(environmentOverrides);
        var result = RunDotnetArgumentListHandler?.Invoke(arguments) ?? (0, string.Empty, string.Empty);
        return Task.FromResult(result);
    }

    public Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo csprojFile, bool includeTransitive = true, CancellationToken cancellationToken = default)
    {
        GetPackageListCallCount++;

        if (ThrowOnGetPackageList)
        {
            throw new InvalidOperationException("Simulated dotnet list package failure.");
        }

        return Task.FromResult(PackageListResult);
    }

    // Delegate file-based csproj modifications to real implementation
    public Task<bool> EnsureEnableMsixToolingAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
        => _real.EnsureEnableMsixToolingAsync(csprojPath, cancellationToken);

    public Task<bool> RemoveWindowsPackageTypeNoneAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
        => _real.RemoveWindowsPackageTypeNoneAsync(csprojPath, cancellationToken);

    public Task<bool> AnnotatePackageReferencesAsync(FileInfo csprojPath, IReadOnlyDictionary<string, string> packageComments, CancellationToken cancellationToken = default)
        => _real.AnnotatePackageReferencesAsync(csprojPath, packageComments, cancellationToken);

    public Task<bool> EnsureAssetContentItemsAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
        => _real.EnsureAssetContentItemsAsync(csprojPath, cancellationToken);
}

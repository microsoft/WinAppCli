// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace WinApp.Cli.Services;

/// <summary>
/// Resolves NuGet package sources, credentials and <c>&lt;packageSourceMapping&gt;</c> from the user's
/// <c>nuget.config</c> hierarchy (rooted at the current working directory). Owns the source/configuration
/// concern for <see cref="NugetService"/> so private/custom feeds and mirrors are honored when restoring
/// SDK packages, and so this logic can be tested independently of package download/version resolution.
/// </summary>
internal sealed class NugetSourceProvider
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // Lazy (ExecutionAndPublication) guarantees the credential-service setup runs exactly once AND that
    // every caller blocks until it has fully completed. Publishing an "initialized" flag before setup
    // finished (e.g. via Interlocked.Exchange) would let a concurrent NuGet operation build its HTTP
    // resources against a not-yet-configured credential service and hit a private feed anonymously.
    private static readonly Lazy<bool> CredentialServiceInitializer = new(() =>
    {
        var nonInteractive = Console.IsInputRedirected
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));

        DefaultCredentialServiceUtility.SetupDefaultCredentialService(Logger, nonInteractive);
        return true;
    });

    // All three caches are Lazy (default ExecutionAndPublication mode: thread-safe, initialized exactly
    // once) rather than plain '??=' fields. NugetSourceProvider is a DI singleton and WorkspaceSetupService
    // resolves versions for many packages concurrently (Task.WhenAll over GetLatestVersionAsync), so a
    // bare '??=' could race two threads into building duplicate providers/mappings or observing a
    // half-initialized cache.
    private readonly Lazy<ISettings> _settings;
    private readonly Lazy<SourceRepositoryProvider> _sourceRepositoryProvider;
    private readonly Lazy<PackageSourceMapping> _packageSourceMapping;

    public NugetSourceProvider(ICurrentDirectoryProvider currentDirectoryProvider)
    {
        _settings = new Lazy<ISettings>(() =>
            NuGet.Configuration.Settings.LoadDefaultSettings(root: currentDirectoryProvider.GetCurrentDirectory()));
        _sourceRepositoryProvider = new Lazy<SourceRepositoryProvider>(() =>
            new SourceRepositoryProvider(new PackageSourceProvider(Settings), Repository.Provider.GetCoreV3()));
        _packageSourceMapping = new Lazy<PackageSourceMapping>(() =>
            NuGet.Configuration.PackageSourceMapping.GetPackageSourceMapping(Settings));
    }

    /// <summary>
    /// The NuGet settings resolved from the user's nuget.config hierarchy. Exposed so consumers can read
    /// the global packages folder, client policy, etc. from the same configuration.
    /// </summary>
    internal ISettings Settings => _settings.Value;

    /// <summary>
    /// Configures NuGet's default credential service so authenticated (private) feeds work using
    /// credentials stored in nuget.config, environment-based credentials, or credential-provider
    /// plugins. Interactive prompting is only enabled for real interactive terminals. Setup runs
    /// exactly once and every caller blocks until it has completed, so concurrent NuGet operations
    /// never observe a half-initialized credential service.
    /// </summary>
    internal static void EnsureCredentialService() => _ = CredentialServiceInitializer.Value;

    private IReadOnlyList<SourceRepository> GetRepositories() =>
        [.. _sourceRepositoryProvider.Value.GetRepositories()];

    private PackageSourceMapping PackageSourceMapping => _packageSourceMapping.Value;

    /// <summary>
    /// Returns the configured package sources eligible to serve <paramref name="packageId"/>, honoring
    /// <c>&lt;packageSourceMapping&gt;</c> when it is enabled. When mapping is enabled but no source is
    /// mapped to the package, an empty list is returned (matching NuGet restore semantics, which fails
    /// rather than falling back to an unmapped feed).
    /// </summary>
    internal IReadOnlyList<SourceRepository> GetRepositoriesForPackage(string packageId)
    {
        var repositories = GetRepositories();

        var mapping = PackageSourceMapping;
        if (!mapping.IsEnabled)
        {
            return repositories;
        }

        var mappedSources = mapping.GetConfiguredPackageSources(packageId);
        if (mappedSources is null || mappedSources.Count == 0)
        {
            return [];
        }

        var allowed = new HashSet<string>(mappedSources, StringComparer.OrdinalIgnoreCase);
        return [.. repositories.Where(r => allowed.Contains(r.PackageSource.Name))];
    }

    /// <summary>
    /// Explains why no source was eligible to serve <paramref name="packageId"/>, distinguishing the
    /// distinct causes — no sources configured at all, the package matching no
    /// <c>&lt;packageSourceMapping&gt;</c> pattern, or the package being mapped to a source that is
    /// disabled/missing — so the error points the user at the right nuget.config fix.
    /// </summary>
    internal string DescribeNoEligibleSources(string packageId)
    {
        // If there are no enabled sources at all, mapping is irrelevant — an empty eligible set can only
        // mean the feed list itself is empty, regardless of whether packageSourceMapping is enabled.
        if (GetRepositories().Count == 0)
        {
            return "no enabled NuGet sources are configured (add or enable a source in the <packageSources> section of your nuget.config)";
        }

        // Sources exist, so packageSourceMapping is what pruned them. Separate "the package matches no
        // mapping pattern" from "the package is mapped, but to a source that isn't enabled/configured"
        // (e.g. the mapped key names a disabled or misspelled source) — the fixes are different.
        var mappedSources = PackageSourceMapping.GetConfiguredPackageSources(packageId);
        if (mappedSources is null || mappedSources.Count == 0)
        {
            return $"no <packageSourceMapping> pattern maps '{packageId}' to a source (add a matching entry in nuget.config)";
        }

        var mapped = string.Join(", ", mappedSources);
        return $"'{packageId}' is mapped to source(s) [{mapped}] that are not enabled/configured (enable or fix the mapped source in the <packageSources> section of your nuget.config)";
    }
}

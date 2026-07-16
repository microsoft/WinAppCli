// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace WinApp.Cli.Services;

/// <summary>
/// Downloads a NuGet package from the user's configured sources and extracts it into the global packages
/// folder. Owns the package-transfer concern for <see cref="NugetService"/> so download/extraction can be
/// tested independently of source resolution and version selection. Source eligibility
/// (<c>&lt;packageSourceMapping&gt;</c>), credentials and settings come from
/// <see cref="NugetSourceProvider"/>.
/// </summary>
internal sealed class NugetPackageDownloader(NugetSourceProvider sourceProvider)
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private readonly NugetSourceProvider _sourceProvider = sourceProvider;

    /// <summary>
    /// Downloads <paramref name="identity"/> from the first configured source that has it and extracts it
    /// into <paramref name="globalPackagesFolder"/> using the standard NuGet on-disk layout. Honors
    /// <c>&lt;packageSourceMapping&gt;</c> for source selection and throws an
    /// <see cref="InvalidOperationException"/> (preserving the underlying source error) when no configured
    /// source can provide the package.
    /// </summary>
    internal async Task DownloadPackageAsync(PackageIdentity identity, string globalPackagesFolder, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        var package = identity.Id;
        var version = identity.Version.ToNormalizedString();
        var clientPolicyContext = ClientPolicyContext.GetClientPolicy(_sourceProvider.Settings, Logger);

        var repos = _sourceProvider.GetRepositoriesForPackage(package);
        Exception? lastError = null;
        string? lastErrorSource = null;

        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Buffer to a temp file rather than memory: SDK packages (e.g. Windows App SDK) are large.
            // Use a random temp path instead of Path.GetTempFileName(): the latter eagerly creates an
            // empty file (which File.Create below immediately overwrites) and throws once ~65,535 temp
            // files already exist in the directory.
            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                bool copied;

                // Capture warning/error diagnostics for this source. CopyNupkgToStreamAsync reports content
                // failures (e.g. a 401/403 on the .nupkg endpoint) through the logger and can then return
                // false instead of throwing; with NullLogger that detail would be lost and the failure
                // misreported as "not found".
                var downloadLogger = new CollectingLogger();
                await using (var fileStream = File.Create(tempFile))
                {
                    try
                    {
                        // Acquiring the resource loads the source's service index, which can throw for an
                        // unreachable/unauthorized source; keep it inside the try so we fail over instead.
                        var byIdResource = await repo.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                        if (byIdResource is null)
                        {
                            continue;
                        }

                        copied = await byIdResource.CopyNupkgToStreamAsync(identity.Id, identity.Version, fileStream, cacheContext, downloadLogger, cancellationToken);
                    }
                    catch (FatalProtocolException ex)
                    {
                        // A canceled request can be surfaced as a FatalProtocolException once the final
                        // HTTP attempt is exhausted; preserve cancellation instead of recording it as a
                        // source failure and later throwing a misleading "download failed" error.
                        cancellationToken.ThrowIfCancellationRequested();

                        // Source unreachable/unauthorized or does not have this package; remember why
                        // (e.g. 401/403/network) and try the next source.
                        lastError = ex;
                        lastErrorSource = repo.PackageSource.Name;
                        continue;
                    }
                }

                if (!copied)
                {
                    // A canceled download can surface as a logged-and-returned false rather than a thrown
                    // OperationCanceledException; keep Ctrl+C as cancellation instead of misreporting it
                    // as a feed failure below.
                    cancellationToken.ThrowIfCancellationRequested();

                    // A false return covers both "this source doesn't have the package" (normal failover)
                    // and a content-endpoint failure (e.g. 401/403) that was retried and logged rather than
                    // thrown. Preserve any captured error so an auth/network failure isn't later reported as
                    // a plain "package/version was not found".
                    if (downloadLogger.LastErrorMessage is not null)
                    {
                        lastError = new InvalidOperationException(downloadLogger.LastErrorMessage);
                        lastErrorSource = repo.PackageSource.Name;
                    }
                    continue;
                }

                await using var readStream = File.OpenRead(tempFile);
                using var addResult = await GlobalPackagesFolderUtility.AddPackageAsync(
                    source: repo.PackageSource.Source,
                    packageIdentity: identity,
                    packageStream: readStream,
                    globalPackagesFolder: globalPackagesFolder,
                    parentId: Guid.Empty,
                    clientPolicyContext: clientPolicyContext,
                    logger: Logger,
                    token: cancellationToken);

                return;
            }
            finally
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // Best-effort cleanup of the temp download.
                }
            }
        }

        // No configured source could provide the package. Surface the underlying reason when we have it
        // so authentication/network failures are distinguishable from a genuinely missing package.
        var sources = string.Join(", ", repos.Select(r => r.PackageSource.Name));
        var baseMessage = string.IsNullOrEmpty(sources)
            ? $"Failed to download {package} {version}: {_sourceProvider.DescribeNoEligibleSources(package)}."
            : $"Failed to download {package} {version} from the configured NuGet sources ({sources}).";

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{baseMessage} Last error from source '{lastErrorSource}': {lastError.Message}", lastError);
        }

        throw new InvalidOperationException($"{baseMessage} The package/version was not found on any configured source.");
    }

    /// <summary>
    /// An <see cref="ILogger"/> that captures the most recent warning/error message emitted by a NuGet
    /// operation. Used to recover the underlying reason (e.g. a 401/403 on a package-content endpoint)
    /// when an API such as <c>CopyNupkgToStreamAsync</c> reports failure by returning <c>false</c> and
    /// logging rather than throwing, so the failure is not later misreported as a plain "not found".
    /// </summary>
    private sealed class CollectingLogger : LoggerBase
    {
        public string? LastErrorMessage { get; private set; }

        public override void Log(ILogMessage message)
        {
            if (message.Level >= LogLevel.Warning)
            {
                LastErrorMessage = message.Message;
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}

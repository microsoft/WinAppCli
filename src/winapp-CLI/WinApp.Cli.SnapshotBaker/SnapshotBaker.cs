// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Helpers;

/// <summary>
/// Produces the corpus that <c>EmbeddedSnapshot</c> serves. Lives in the build-time
/// <c>WinApp.Cli.SnapshotBaker</c> tool rather than in the CLI, so regenerating the
/// corpus is not reachable from the shipped product. Invoked by
/// <c>scripts/build-cli.ps1</c> on the release path, writing uncompressed, indented
/// JSON that is committed to the repo; the build Brotli-compresses it into the binary.
///
/// The committed form is deliberately uncompressed: a compressed blob in git cannot be
/// reviewed, and the whole point of baking is that corpus regressions — malformed XAML,
/// an upstream layout change that guts a source — show up as a diff a human can read
/// before they ship.
/// </summary>
internal static class SnapshotBaker
{
    /// <summary>Filename for a provider's committed snapshot. The compressed sibling that
    /// actually ships is this name plus <c>.br</c>.</summary>
    public static string SnapshotFileName(string providerId) => $"snapshot-{providerId}.json";

    public const string ManifestFileName = "snapshot-manifest.json";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Write <paramref name="json"/> in both committed forms: readable text for review and
    /// diffing, and the Brotli blob that is embedded in the binary. They are written
    /// together so they cannot diverge at the source;
    /// <c>EmbeddedSnapshotTests.CompressedSnapshot_MatchesCommittedJson</c> enforces
    /// that they stay together in the repo.
    /// </summary>
    private static async Task WriteSnapshotPairAsync(string path, string json, CancellationToken cancellationToken)
    {
        await PathSafety.AtomicWriteAllTextAsync(path, json, Utf8NoBom, cancellationToken).ConfigureAwait(false);

        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await brotli.WriteAsync(Utf8NoBom.GetBytes(json), cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(path + ".br", compressed.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch every provider fresh and write the snapshot set to <paramref name="outputDirectory"/>.
    /// Reports per-provider counts through <paramref name="report"/>.
    /// </summary>
    /// <returns>
    /// The ids of providers that could not be baked. Empty means a complete bake. A
    /// partial result is never written as if it were complete — the caller decides
    /// whether to fail or keep the previous committed corpus.
    /// </returns>
    public static async Task<IReadOnlyList<string>> BakeAsync(
        string outputDirectory,
        Action<string> report,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        // Providers cache to disk as a side effect of loading. Point them at a throwaway
        // directory so a bake never reads from — or writes to — the developer's real
        // find-ui cache, which would let a warm local cache masquerade as a fresh fetch.
        var scratchCache = Path.Combine(Path.GetTempPath(), $"winapp-bake-{Guid.NewGuid():N}");
        var failures = new List<string>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        try
        {
            foreach (var provider in ProviderRegistry.CreateProviders(scratchCache))
            {
                cancellationToken.ThrowIfCancellationRequested();
                report($"Fetching {provider.DisplayName}…");

                var data = await provider.LoadAsync(forceRefresh: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                // LoadAsync falls back through cache and embedded snapshot when a fetch
                // fails. For a bake that fallback is a trap: it would re-emit the corpus
                // already in the binary and look like a successful refresh. Only a genuine
                // network result is acceptable here.
                if (data.Origin != CorpusOrigin.Network || data.Scenarios.Length == 0)
                {
                    report($"  FAILED: {provider.DisplayName} returned no fetched data.");
                    failures.Add(provider.Id);
                    continue;
                }

                var snapshot = new ProviderSnapshot
                {
                    Scenarios = data.Scenarios,
                    Tags = new SortedDictionary<string, string[]>(data.Tags, StringComparer.Ordinal),
                    Keywords = new SortedDictionary<string, string[]>(data.Keywords, StringComparer.Ordinal)
                };

                var path = Path.Combine(outputDirectory, SnapshotFileName(provider.Id));
                await WriteSnapshotPairAsync(
                    path,
                    JsonSerializer.Serialize(snapshot, ControlsSnapshotWriteContext.Default.ProviderSnapshot),
                    cancellationToken).ConfigureAwait(false);

                counts[provider.Id] = data.Scenarios.Length;
                report($"  {provider.Id}: {data.Scenarios.Length} scenarios → {Path.GetFileName(path)}");
            }

            if (failures.Count > 0)
            {
                return failures;
            }

            var manifest = new SnapshotManifest
            {
                BakedAtUtc = DateTime.UtcNow,
                CacheVersion = Controls.CacheVersion.Current,
                ScenarioCounts = counts
            };

            await PathSafety.AtomicWriteAllTextAsync(
                Path.Combine(outputDirectory, ManifestFileName),
                JsonSerializer.Serialize(manifest, ControlsSnapshotWriteContext.Default.SnapshotManifest),
                Utf8NoBom,
                cancellationToken).ConfigureAwait(false);

            report($"Baked {counts.Count} sources at cache version {Controls.CacheVersion.Current}.");
            return failures;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratchCache))
                {
                    Directory.Delete(scratchCache, recursive: true);
                }
            }
            catch { /* scratch cleanup is best-effort */ }
        }
    }
}

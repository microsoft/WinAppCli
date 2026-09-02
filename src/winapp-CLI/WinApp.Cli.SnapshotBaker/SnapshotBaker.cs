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
/// <c>scripts/build-cli.ps1</c> on the release path.
///
/// Only the Brotli blob is written and committed. The corpus is a backup: whenever the
/// network is reachable the CLI serves live data, so the embedded copy exists for the
/// offline case and is refreshed wholesale at every release — which makes release time,
/// not any committed text file, the source of truth for what it contains.
/// <c>snapshot-manifest.json</c> stays readable and committed, so scenario counts per
/// source remain reviewable in a diff without carrying roughly 900 KB of duplicated JSON.
/// </summary>
internal static class SnapshotBaker
{
    /// <summary>Filename for a provider's committed snapshot — the Brotli blob that is
    /// embedded in the binary, and the only per-provider corpus file written.</summary>
    public static string SnapshotFileName(string providerId) => $"snapshot-{providerId}.json.br";

    public const string ManifestFileName = "snapshot-manifest.json";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Brotli-compress <paramref name="json"/> to <paramref name="path"/>. Compression is
    /// deterministic for identical input, which is what lets the drift job compare a fresh
    /// bake against the committed blob without a second, uncompressed copy in the repo.
    /// </summary>
    private static async Task WriteSnapshotAsync(string path, string json, CancellationToken cancellationToken)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await brotli.WriteAsync(Utf8NoBom.GetBytes(json), cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(path, compressed.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch every provider fresh and write the snapshot set to <paramref name="outputDirectory"/>.
    /// Reports per-provider counts through <paramref name="report"/>.
    /// </summary>
    /// <returns>
    /// The ids of providers that could not be baked. Empty means a complete bake. A
    /// partial result is never written as if it were complete — the whole set is built in a
    /// staging directory and only moved into <paramref name="outputDirectory"/> once every
    /// provider and the manifest have succeeded, so a failed, cancelled, or crashed bake
    /// leaves the previous committed corpus exactly as it was.
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
        var scratchCache = Path.Join(Path.GetTempPath(), $"winapp-bake-{Guid.NewGuid():N}");

        // Snapshots land here first and are moved into place only after the whole set
        // succeeds. Writing them straight into outputDirectory would leave a provider that
        // succeeded next to a manifest from the previous bake if a later provider failed —
        // fresh scenarios stamped with a stale bake time. Staged *inside* outputDirectory
        // so the publish below is a same-volume move rather than a cross-volume copy, and
        // dot-prefixed so it can't be picked up by the `snapshot-*` globs in the csproj or
        // in build-cli.ps1.
        var staging = Path.Join(outputDirectory, $".bake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

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

                var path = Path.Join(staging, SnapshotFileName(provider.Id));
                await WriteSnapshotAsync(
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
                Path.Join(staging, ManifestFileName),
                JsonSerializer.Serialize(manifest, ControlsSnapshotWriteContext.Default.SnapshotManifest),
                Utf8NoBom,
                cancellationToken).ConfigureAwait(false);

            Publish(staging, outputDirectory);

            report($"Baked {counts.Count} sources at cache version {Controls.CacheVersion.Current}.");
            return failures;
        }
        finally
        {
            TryDeleteDirectory(scratchCache);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Move a complete staged bake into its final home, overwriting the previous corpus.
    /// Called only once every provider and the manifest have been written, so the
    /// destination never holds a mix of the two bakes for longer than this loop.
    /// </summary>
    private static void Publish(string staging, string outputDirectory)
    {
        foreach (var staged in Directory.GetFiles(staging))
        {
            File.Move(staged, Path.Join(outputDirectory, Path.GetFileName(staged)), overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch { /* cleanup of a temp directory is best-effort */ }
    }
}

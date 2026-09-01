// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// The embedded corpus floor. Each provider's baked scenarios ship in the binary as a
/// Brotli-compressed resource (<c>snapshot-{providerId}.json.br</c>), with an
/// uncompressed <c>snapshot-manifest.json</c> describing when they were baked and by
/// which <see cref="CacheVersion"/>.
///
/// This exists because <c>find-ui</c>'s primary audience — coding agents in sandboxes —
/// frequently cannot reach <c>raw.githubusercontent.com</c>, which every fetcher depends
/// on. Without a floor the command is not degraded there, it is entirely non-functional.
/// The snapshot is a floor, never a ceiling: a successful network fetch always wins, and
/// a per-user cache newer than <see cref="BakedAtUtc"/> wins too (see
/// <see cref="CachedProviderBase"/>).
///
/// Loading is lazy and per-provider: a search that only touches Gallery never pays to
/// decompress the Toolkit corpus. Results are memoized because the payload is immutable
/// and decompression is the expensive part.
/// </summary>
internal static class EmbeddedSnapshot
{
    private const string ManifestResourceName = "snapshot-manifest.json";

    private static readonly Lazy<SnapshotManifest?> LazyManifest =
        new(LoadManifest, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Dictionary<string, ProviderData?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Manifest for the embedded set, or <c>null</c> when no snapshot ships
    /// (or it fails to parse).</summary>
    public static SnapshotManifest? Manifest => LazyManifest.Value;

    /// <summary>
    /// When the embedded corpus was pulled from upstream, or <c>null</c> if no usable
    /// snapshot ships. Compared against the per-user cache timestamp to decide which
    /// copy reflects more recent upstream data.
    /// </summary>
    public static DateTime? BakedAtUtc
    {
        get
        {
            var manifest = Manifest;
            if (manifest is null) return null;
            return manifest.BakedAtUtc.Kind == DateTimeKind.Utc
                ? manifest.BakedAtUtc
                : manifest.BakedAtUtc.ToUniversalTime();
        }
    }

    /// <summary>
    /// The baked corpus for <paramref name="providerId"/>, or <c>null</c> when none
    /// ships for it. Never throws: a missing, truncated, or unparseable resource
    /// degrades to "no floor" rather than taking the command down, since the caller
    /// still has the network and cache paths to fall back on.
    /// </summary>
    public static ProviderData? TryLoad(string providerId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(providerId, out var memoized))
            {
                return memoized;
            }

            var loaded = Load(providerId);
            Cache[providerId] = loaded;
            return loaded;
        }
    }

    private static ProviderData? Load(string providerId)
    {
        // A snapshot baked by different extraction logic must not be served: its tags,
        // ids, and cleaning would silently disagree with everything else in the process.
        // Rejecting here is defence in depth — the manifest test fails the build if a
        // CacheVersion bump ships without a re-bake, so this should be unreachable in a
        // released binary.
        var manifest = Manifest;
        if (manifest is null || !string.Equals(manifest.CacheVersion, CacheVersion.Current, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var compressed = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"snapshot-{providerId}.json.br");
            if (compressed is null) return null;

            using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
            var snapshot = JsonSerializer.Deserialize(brotli, ControlsJsonContext.Default.ProviderSnapshot);
            if (snapshot is null || snapshot.Scenarios.Length == 0) return null;

            return new ProviderData(
                snapshot.Scenarios,
                new Dictionary<string, string[]>(snapshot.Tags, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string[]>(snapshot.Keywords, StringComparer.OrdinalIgnoreCase),
                CorpusOrigin.Embedded);
        }
        catch
        {
            return null;
        }
    }

    private static SnapshotManifest? LoadManifest()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ManifestResourceName);
            if (stream is null) return null;
            return JsonSerializer.Deserialize(stream, ControlsJsonContext.Default.SnapshotManifest);
        }
        catch
        {
            return null;
        }
    }
}

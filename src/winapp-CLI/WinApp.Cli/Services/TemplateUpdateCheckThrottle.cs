// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

/// <summary>
/// File-backed implementation of <see cref="ITemplateUpdateCheckThrottle"/>. Persists the last
/// staleness-check timestamp in the global winapp directory, mirroring the '.update-check' cache used
/// by <see cref="UpdateNotificationService"/> for the CLI self-update notice.
/// </summary>
internal sealed class TemplateUpdateCheckThrottle(
    IWinappDirectoryService winappDirectoryService,
    ILogger<TemplateUpdateCheckThrottle> logger) : ITemplateUpdateCheckThrottle
{
    private const string CacheFileName = ".template-update-check";
    private const int CheckIntervalHours = 24;

    // Seam so tests can advance the clock without waiting a real day.
    internal Func<DateTimeOffset> UtcNowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    // Cache file format (one value per line):
    //   Line 0: last-check timestamp (round-trip "O" format, UTC)
    //   Line 1: installed pack version the check was performed against
    //   Line 2: newest available version found (empty when up-to-date)

    public bool TryGetRecentLatest(string installedVersion, out string? latestVersion)
    {
        latestVersion = null;
        try
        {
            var cache = ReadCache(GetCacheFile());

            // A recent check only counts when it was performed against the same installed version:
            // if the pack changed (e.g. the user updated it), the cached "latest" no longer applies.
            if (!cache.LastCheck.HasValue
                || !string.Equals(cache.InstalledVersion, installedVersion, StringComparison.OrdinalIgnoreCase)
                || (UtcNowProvider() - cache.LastCheck.Value).TotalHours >= CheckIntervalHours)
            {
                return false;
            }

            latestVersion = string.IsNullOrEmpty(cache.LatestVersion) ? null : cache.LatestVersion;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read template update-check cache; treating as due.");
            return false;
        }
    }

    public void Record(string installedVersion, string? latestVersion)
    {
        try
        {
            WriteCache(
                GetCacheFile(),
                new Entry(UtcNowProvider(), installedVersion, latestVersion ?? ""));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to write template update-check cache.");
        }
    }

    private FileInfo GetCacheFile()
    {
        var globalDir = winappDirectoryService.GetGlobalWinappDirectory();
        return new FileInfo(Path.Combine(globalDir.FullName, CacheFileName));
    }

    private static Entry ReadCache(FileInfo cacheFile)
    {
        if (!cacheFile.Exists)
        {
            return Entry.Empty;
        }

        var lines = File.ReadAllLines(cacheFile.FullName);

        DateTimeOffset? lastCheck = null;
        if (lines.Length >= 1
            && DateTimeOffset.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            lastCheck = parsed;
        }

        var installedVersion = lines.Length >= 2 ? lines[1] : "";
        var latestVersion = lines.Length >= 3 ? lines[2] : "";

        return new Entry(lastCheck, installedVersion, latestVersion);
    }

    private void WriteCache(FileInfo cacheFile, Entry entry)
    {
        cacheFile.Directory?.Create();

        // Write to a temp file then move for atomic replacement.
        var tempPath = cacheFile.FullName + ".tmp";
        var content = $"{entry.LastCheck?.ToString("O", CultureInfo.InvariantCulture) ?? ""}\n{entry.InstalledVersion}\n{entry.LatestVersion}";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, cacheFile.FullName, overwrite: true);

        try
        {
            cacheFile.Refresh();
            cacheFile.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to hide template update-check cache file.");
        }
    }

    private sealed record Entry(DateTimeOffset? LastCheck, string InstalledVersion, string LatestVersion)
    {
        public static readonly Entry Empty = new(null, "", "");
    }
}

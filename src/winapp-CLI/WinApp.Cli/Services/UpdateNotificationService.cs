// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class UpdateNotificationService(
    IWinappDirectoryService winappDirectoryService,
    IAnsiConsole ansiConsole,
    ILogger<UpdateNotificationService> logger) : IUpdateNotificationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string GitHubApiLatestRelease = "https://api.github.com/repos/microsoft/winappcli/releases/latest";
    private const string UpdateCheckFileName = ".update-check";
    private const int CheckIntervalHours = 24;

    public async Task CheckAndNotifyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cacheFile = GetUpdateCheckFile();

            // Read cache to see if we already checked recently
            if (cacheFile.Exists)
            {
                var lines = await File.ReadAllLinesAsync(cacheFile.FullName, cancellationToken);
                if (lines.Length >= 1
                    && DateTimeOffset.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastCheck)
                    && (DateTimeOffset.UtcNow - lastCheck).TotalHours < CheckIntervalHours)
                {
                    // Already checked and notified within the last 24 hours — skip
                    return;
                }
            }

            var latestVersion = await GetLatestVersionAsync(cancellationToken);
            var currentVersion = VersionHelper.GetVersionString();
            string? newVersion = null;

            if (latestVersion != null && IsNewerVersion(latestVersion, currentVersion))
            {
                newVersion = latestVersion;
                DisplayUpdateNotification(newVersion);
            }

            // Write cache with timestamp so we don't check again for 24 hours
            WriteCacheFile(cacheFile, newVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Silent failure — never disrupt the user's command
        }
    }

    internal async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        // Allow overriding the latest version for testing (skips GitHub API call)
        var overrideVersion = Environment.GetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION");
        if (!string.IsNullOrEmpty(overrideVersion))
        {
            return overrideVersion;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestRelease);
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("WinAppCLI");

            using var response = await Http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tagName))
            {
                return null;
            }

            // Strip leading "v" prefix (e.g. "v0.3.0" → "0.3.0")
            return tagName.StartsWith('v') ? tagName[1..] : tagName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to check for CLI updates: {Error}", ex.Message);
            return null;
        }
    }

    private void DisplayUpdateNotification(string newVersion)
    {
        var upgradeHint = DetectInstallChannel() switch
        {
            InstallChannel.Npm => "npm update -g @microsoft/winappcli",
            InstallChannel.NuGet => "visit https://github.com/microsoft/winappcli/releases",
            _ => "visit https://github.com/microsoft/winappcli/releases"
        };

        ansiConsole.MarkupLine($"[yellow]v{newVersion} is available. To update, {Markup.Escape(upgradeHint)}.[/]");
    }

    internal static bool IsNewerVersion(string latest, string current)
    {
        static bool TryParseSemVer(string value, out Version coreVersion, out string? prerelease)
        {
            coreVersion = new Version(0, 0);
            prerelease = null;

            var plusIdx = value.IndexOf('+');
            if (plusIdx >= 0)
            {
                value = value[..plusIdx];
            }

            var dashIdx = value.IndexOf('-');
            if (dashIdx >= 0)
            {
                prerelease = value[(dashIdx + 1)..];
                value = value[..dashIdx];
            }

            return Version.TryParse(value, out coreVersion!);
        }

        if (!TryParseSemVer(latest, out var latestCore, out var latestPre))
        {
            return false;
        }

        if (!TryParseSemVer(current, out var currentCore, out var currentPre))
        {
            return false;
        }

        var coreCompare = latestCore.CompareTo(currentCore);
        if (coreCompare != 0)
        {
            return coreCompare > 0;
        }

        // Same core version: a stable release (no pre-release) is newer than a pre-release
        if (currentPre != null && latestPre == null)
        {
            return true;
        }

        return false;
    }

    private static InstallChannel DetectInstallChannel()
    {
        // Check caller env var (set by wrapper scripts via --caller option)
        var caller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        if (string.Equals(caller, "npm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(caller, "nodejs-package", StringComparison.OrdinalIgnoreCase))
        {
            return InstallChannel.Npm;
        }
        if (string.Equals(caller, "nuget-package", StringComparison.OrdinalIgnoreCase))
        {
            return InstallChannel.NuGet;
        }

        // Check exe path heuristics
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            if (exePath.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                return InstallChannel.Npm;
            }
            if (exePath.Contains(".nuget", StringComparison.OrdinalIgnoreCase))
            {
                return InstallChannel.NuGet;
            }
        }

        return InstallChannel.StandaloneExe;
    }

    private FileInfo GetUpdateCheckFile()
    {
        var globalDir = winappDirectoryService.GetGlobalWinappDirectory();
        return new FileInfo(Path.Combine(globalDir.FullName, UpdateCheckFileName));
    }

    private void WriteCacheFile(FileInfo cacheFile, string? newVersion)
    {
        try
        {
            cacheFile.Directory?.Create();
            File.WriteAllText(cacheFile.FullName, $"{DateTime.UtcNow:O}\n{newVersion ?? ""}");
            cacheFile.Refresh();
            cacheFile.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to write update check cache: {Error}", ex.Message);
        }
    }
}

internal enum InstallChannel
{
    Msix,
    StandaloneExe,
    Npm,
    NuGet
}

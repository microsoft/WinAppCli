// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class CliUpgradeService(
    IWinappDirectoryService winappDirectoryService,
    IStatusService statusService,
    ILogger<CliUpgradeService> logger) : ICliUpgradeService
{
    private static readonly HttpClient Http = new();
    private const string GitHubApiLatestRelease = "https://api.github.com/repos/microsoft/winappcli/releases/latest";
    private const string UpdateCheckFileName = ".update-check";
    private const int CheckIntervalHours = 24;

    public InstallChannel DetectInstallChannel()
    {
        // Check for MSIX package identity via GetCurrentPackageFullName
        if (HasMsixPackageIdentity())
        {
            return InstallChannel.Msix;
        }

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

    public async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
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
        catch (Exception ex)
        {
            logger.LogDebug("Failed to check for CLI updates: {Error}", ex.Message);
            return null;
        }
    }

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
        catch
        {
            // Silent failure — never disrupt the user's command
        }
    }

    public async Task<int> UpgradeAsync(CancellationToken cancellationToken = default)
    {
        var channel = DetectInstallChannel();

        switch (channel)
        {
            case InstallChannel.Npm:
                logger.LogInformation("winapp was installed via npm. To upgrade, run:");
                logger.LogInformation("  npm update -g @microsoft/winappcli");
                return 0;

            case InstallChannel.NuGet:
                logger.LogInformation("winapp was installed via NuGet. Update the Microsoft.Windows.SDK.BuildTools.WinApp NuGet package in your project.");
                return 0;

            case InstallChannel.Msix:
                return await UpgradeMsixAsync(cancellationToken);

            case InstallChannel.StandaloneExe:
                return await UpgradeExeAsync(cancellationToken);

            default:
                logger.LogError("Unknown install channel. Cannot upgrade automatically.");
                return 1;
        }
    }

    private async Task<int> UpgradeMsixAsync(CancellationToken cancellationToken)
    {
        return await statusService.ExecuteWithStatusAsync<string>(
            "Upgrading winapp CLI (MSIX)...",
            async (taskContext, ct) =>
            {
                var arch = GetArchitectureSuffix();
                var msixFileName = $"winappcli_{arch}.msix";

                var (downloadUrl, version) = await GetReleaseAssetUrlAsync(msixFileName, ct);

                taskContext.AddDebugMessage($"Downloading {msixFileName} v{version}...");
                var tempDir = Path.Combine(Path.GetTempPath(), $"winapp-upgrade-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                var msixPath = Path.Combine(tempDir, msixFileName);

                try
                {
                    using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var fs = File.Create(msixPath);
                        await response.Content.CopyToAsync(fs, ct);
                    }

                    taskContext.AddDebugMessage("Installing MSIX package...");

                    // Use PackageManager to install the MSIX
                    var packageManager = new Windows.Management.Deployment.PackageManager();
                    var deploymentResult = await packageManager.AddPackageAsync(
                        new Uri(Path.GetFullPath(msixPath)),
                        null,
                        Windows.Management.Deployment.DeploymentOptions.ForceApplicationShutdown);

                    if (!string.IsNullOrEmpty(deploymentResult.ErrorText))
                    {
                        throw new InvalidOperationException($"MSIX installation failed: {deploymentResult.ErrorText}");
                    }

                    // Clear the update check cache
                    ClearCacheFile();

                    return (0, $"Successfully upgraded to v{version}");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            },
            cancellationToken);
    }

    private async Task<int> UpgradeExeAsync(CancellationToken cancellationToken)
    {
        return await statusService.ExecuteWithStatusAsync<string>(
            "Upgrading winapp CLI...",
            async (taskContext, ct) =>
            {
                var arch = GetArchitectureSuffix();
                var zipFileName = $"winappcli-{arch}.zip";

                var (downloadUrl, version) = await GetReleaseAssetUrlAsync(zipFileName, ct);

                taskContext.AddDebugMessage($"Downloading {zipFileName} v{version}...");
                var tempDir = Path.Combine(Path.GetTempPath(), $"winapp-upgrade-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                var zipPath = Path.Combine(tempDir, zipFileName);
                var extractDir = Path.Combine(tempDir, "extracted");

                try
                {
                    using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var fs = File.Create(zipPath);
                        await response.Content.CopyToAsync(fs, ct);
                    }

                    taskContext.AddDebugMessage("Extracting...");
                    await ZipFile.ExtractToDirectoryAsync(zipPath, extractDir, overwriteFiles: true, cancellationToken: ct);

                    // Find the new winapp.exe in extracted directory
                    var newExePath = Directory.GetFiles(extractDir, "winapp.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new FileNotFoundException("winapp.exe not found in downloaded archive");

                    var currentExePath = Environment.ProcessPath
                        ?? throw new InvalidOperationException("Cannot determine current executable path");

                    taskContext.AddDebugMessage("Swapping executable...");
                    var backupPath = currentExePath + ".old";

                    // Remove any leftover backup from a previous upgrade
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    // Rename running exe to .old (Windows allows renaming a locked file)
                    File.Move(currentExePath, backupPath);

                    try
                    {
                        File.Move(newExePath, currentExePath);
                    }
                    catch
                    {
                        // Roll back if the move fails
                        File.Move(backupPath, currentExePath);
                        throw;
                    }

                    // Try to clean up the old exe (may fail if still locked)
                    try { File.Delete(backupPath); } catch { }

                    // Clear the update check cache
                    ClearCacheFile();

                    return (0, $"Successfully upgraded to v{version}");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            },
            cancellationToken);
    }

    private static async Task<(string DownloadUrl, string Version)> GetReleaseAssetUrlAsync(string assetFileName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestRelease);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("WinAppCLI");

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var tagName = doc.RootElement.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("Could not determine latest release version.");

        var version = tagName.StartsWith('v') ? tagName[1..] : tagName;

        string? downloadUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (string.Equals(name, assetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        downloadUrl ??= $"https://github.com/microsoft/winappcli/releases/download/{tagName}/{assetFileName}";

        return (downloadUrl, version);
    }

    private void DisplayUpdateNotification(string newVersion)
    {
        var channel = DetectInstallChannel();
        var upgradeHint = channel switch
        {
            InstallChannel.Npm => "npm update -g @microsoft/winappcli",
            InstallChannel.NuGet => "update the Microsoft.Windows.SDK.BuildTools.WinApp NuGet package",
            _ => "winapp upgrade"
        };

        AnsiConsole.MarkupLine($"[yellow]v{newVersion} is available. Run `{Markup.Escape(upgradeHint)}` to update.[/]");
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        // Strip prerelease/build metadata suffixes (e.g. "0.2.2-beta.1+abc" → "0.2.2")
        static string StripSuffix(string v)
        {
            var dashIdx = v.IndexOf('-');
            if (dashIdx >= 0)
            {
                v = v[..dashIdx];
            }

            var plusIdx = v.IndexOf('+');
            if (plusIdx >= 0)
            {
                v = v[..plusIdx];
            }

            return v;
        }

        if (Version.TryParse(StripSuffix(latest), out var latestVer) && Version.TryParse(StripSuffix(current), out var currentVer))
        {
            return latestVer > currentVer;
        }
        return false;
    }

    private static bool HasMsixPackageIdentity()
    {
        try
        {
            uint length = 0;
            unsafe
            {
                var result = Windows.Win32.PInvoke.GetCurrentPackageFullName(ref length, null);
                // ERROR_INSUFFICIENT_BUFFER (122) means the app has package identity
                // APPMODEL_ERROR_NO_PACKAGE (15700) means it does not
                return result == Windows.Win32.Foundation.WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string GetArchitectureSuffix()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.OSArchitecture}")
        };
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

    private void ClearCacheFile()
    {
        try
        {
            var cacheFile = GetUpdateCheckFile();
            if (cacheFile.Exists)
            {
                cacheFile.Delete();
            }
        }
        catch { }
    }
}

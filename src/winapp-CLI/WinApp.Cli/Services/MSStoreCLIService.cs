// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace WinApp.Cli.Services;

internal class MSStoreCLIService(IAnsiConsole ansiConsole, IWinappDirectoryService winappDirectoryService, ILogger<MSStoreCLIService> logger) : IMSStoreCLIService
{
    private static readonly HttpClient Http = new();
    private const string ExeName = "msstore.exe";

    public async Task EnsureMSStoreCLIAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsMSStoreCLIAvailable())
        {
            var confirm = await ansiConsole.PromptAsync(new ConfirmationPrompt("MSStoreCLI not installed - download and install MSStore Developer CLI?"), cancellationToken);
            if (!confirm)
            {
                throw new InvalidOperationException("MSStoreCLI is required but not installed.");
            }

            await DownloadAndInstallAsync(cancellationToken);

            logger.LogInformation("MSStoreCLI installation completed.");
        }
    }

    private async Task DownloadAndInstallAsync(CancellationToken cancellationToken)
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.OSArchitecture}")
        };

        var downloadUrl = $"https://github.com/microsoft/msstore-cli/releases/latest/download/MSStoreCLI-win-{arch}.zip";
        logger.LogDebug("Downloading MSStoreCLI from {Url}", downloadUrl);

        var installDir = GetInstallDirectory();
        Directory.CreateDirectory(installDir);

        var zipPath = Path.Combine(installDir, "MSStoreCLI.zip");

        try
        {
            using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await response.Content.CopyToAsync(fs, cancellationToken);
            }

            logger.LogDebug("Extracting MSStoreCLI to {InstallDir}", installDir);
            ZipFile.ExtractToDirectory(zipPath, installDir, overwriteFiles: true);

            logger.LogDebug("MSStoreCLI installed to {InstallDir}", installDir);
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    public string GetMSStoreCLIPath()
    {
        return Path.Combine(GetInstallDirectory(), ExeName);
    }

    private string GetInstallDirectory()
    {
        return Path.Combine(winappDirectoryService.GetGlobalWinappDirectory().FullName, "tools", "msstore");
    }

    private bool IsMSStoreCLIAvailable()
    {
        var exePath = GetMSStoreCLIPath();
        var exists = File.Exists(exePath);
        if (exists)
        {
            logger.LogDebug("MSStoreCLI found at {ExePath}", exePath);
        }
        return exists;
    }
}

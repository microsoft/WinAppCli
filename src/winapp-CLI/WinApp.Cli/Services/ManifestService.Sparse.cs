// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Sparse identity manifest generation: preparing manifest metadata (inferring from the exe and
/// prompting), generating the identity-only appxmanifest.xml, and applying the exe's icon as the
/// placeholder logo. Split out of ManifestService.cs to keep each file within the repository's
/// file-size guidance.
/// </summary>
internal partial class ManifestService
{
    public async Task<ManifestGenerationInfo> PrepareSparseManifestInfoAsync(
        DirectoryInfo outputDirectory,
        FileInfo executable,
        string? packageName,
        string? publisherName,
        bool useDefaults,
        CancellationToken cancellationToken = default)
    {
        // Infer the package version from the executable's file version, falling back to 1.0.0.0.
        var inferredVersion = "1.0.0.0";
        try
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(executable.FullName);
            inferredVersion = NormalizeManifestVersion(fileVersionInfo.FileVersion) ?? "1.0.0.0";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: keep the default version if the exe has no readable version info.
        }

        // Infer package name / publisher / description from the exe and (unless --use-defaults)
        // prompt the user to accept or override each value. This runs OUTSIDE any status spinner
        // because Spectre.Console forbids an interactive prompt during a live progress display.
        return await PromptForManifestInfoAsync(
            outputDirectory,
            packageName,
            publisherName,
            inferredVersion,
            description: null,
            executable: executable.FullName,
            useDefaults,
            cancellationToken);
    }

    public async Task<SparseInitResult> GenerateSparseIdentityManifestAsync(
        DirectoryInfo outputDirectory,
        FileInfo executable,
        ManifestGenerationInfo info,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        outputDirectory.Create();

        // Generate the sparse identity manifest as appxmanifest.xml, substituting the concrete
        // external exe name for the $targetnametoken$ build token so it can be packed directly.
        await manifestTemplateService.GenerateCompleteManifestAsync(
            outputDirectory,
            info.PackageName,
            info.PublisherName,
            info.Version,
            ManifestTemplates.Sparse,
            info.Description,
            taskContext,
            manifestFileName: "appxmanifest.xml",
            executableName: executable.Name,
            cancellationToken: cancellationToken);

        var manifestPath = new FileInfo(Path.Combine(outputDirectory.FullName, "appxmanifest.xml"));
        var assetsDirectory = new DirectoryInfo(Path.Combine(outputDirectory.FullName, "Assets"));

        // Best-effort: extract the app icon from the exe to replace the placeholder assets.
        await TryApplyExtractedLogoAsync(manifestPath, executable, taskContext, cancellationToken);

        return new SparseInitResult(manifestPath, info, assetsDirectory);
    }

    /// <summary>
    /// Extracts the jumbo icon from an executable and applies it to the manifest's assets.
    /// Silently no-ops if extraction fails.
    /// </summary>
    private async Task TryApplyExtractedLogoAsync(FileInfo manifestPath, FileInfo executable, TaskContext taskContext, CancellationToken cancellationToken)
    {
        string? extractedLogoPath = null;
        try
        {
            extractedLogoPath = ExtractExeIconToTempPng(executable.FullName);
            if (extractedLogoPath == null)
            {
                return;
            }

            await UpdateManifestAssetsAsync(manifestPath, new FileInfo(extractedLogoPath), taskContext, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"Could not extract logo from executable: {ex.Message}");
        }
        finally
        {
            if (extractedLogoPath != null)
            {
                try
                {
                    File.Delete(extractedLogoPath);
                    Directory.Delete(Path.GetDirectoryName(extractedLogoPath)!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best-effort cleanup
                }
            }
        }
    }
}

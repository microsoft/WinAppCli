// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Represents an asset reference extracted from an AppxManifest, including its path and dimensions.
/// </summary>
/// <param name="RelativePath">The relative path to the asset from the manifest directory (e.g., "Assets\StoreLogo.png")</param>
/// <param name="BaseWidth">The base width in pixels for the asset</param>
/// <param name="BaseHeight">The base height in pixels for the asset</param>
internal record ManifestAssetReference(string RelativePath, int BaseWidth, int BaseHeight);

internal interface IImageAssetService
{
    /// <summary>
    /// Generates MSIX image assets from a source image and saves them to the specified directory.
    /// Uses the default manifest asset references to generate the standard MSIX asset set.
    /// </summary>
    /// <param name="sourceImagePath">Path to the source image file</param>
    /// <param name="outputDirectory">Directory where generated assets will be saved</param>
    /// <param name="taskContext">Task context for status messages</param>
    /// <param name="lightImagePath">Optional path to the source image for light theme variants</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when all assets are generated</returns>
    Task GenerateAssetsAsync(
        FileInfo sourceImagePath,
        DirectoryInfo outputDirectory,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates MSIX image assets from a source image based on asset references from the manifest.
    /// Creates the base asset, scale variants, targetsize variants, and optional light-theme variants.
    /// </summary>
    /// <param name="sourceImagePath">Path to the source image file</param>
    /// <param name="manifestDirectory">Directory where the manifest is located (assets are relative to this)</param>
    /// <param name="assetReferences">Asset references extracted from the manifest</param>
    /// <param name="taskContext">Task context for status messages</param>
    /// <param name="lightImagePath">Optional path to the source image for light theme variants</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when all assets are generated</returns>
    Task GenerateAssetsFromManifestAsync(
        FileInfo sourceImagePath,
        DirectoryInfo manifestDirectory,
        IReadOnlyList<ManifestAssetReference> assetReferences,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a multi-resolution ICO file from the source image.
    /// </summary>
    /// <param name="sourceImagePath">Path to the source image file</param>
    /// <param name="outputPath">Output path for the generated ICO file</param>
    /// <param name="taskContext">Task context for status messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when the ICO file is generated</returns>
    Task GenerateIcoAsync(FileInfo sourceImagePath, string outputPath, TaskContext taskContext, CancellationToken cancellationToken = default);
}

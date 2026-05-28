// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IBundleValidationService
{
    /// <summary>
    /// Validates consistency across multiple bundle slices. Checks that Identity (excluding arch),
    /// Capabilities, Dependencies, and Applications are consistent across all slices.
    /// Also validates architecture constraints (no duplicates, no all-neutral, no unknown).
    /// </summary>
    /// <param name="sliceManifests">Finalized manifest documents per slice.</param>
    /// <param name="detectedArchitectures">Detected architecture per slice (parallel to sliceManifests).</param>
    /// <param name="inputFolders">Input folders per slice (for error messages).</param>
    /// <returns>A list of validation errors. Empty list means validation passed.</returns>
    IReadOnlyList<BundleValidationError> Validate(
        IReadOnlyList<AppxManifestDocument> sliceManifests,
        IReadOnlyList<string> detectedArchitectures,
        IReadOnlyList<DirectoryInfo> inputFolders);
}

/// <summary>
/// Represents a single cross-slice validation error.
/// </summary>
internal record BundleValidationError(string Field, string Message, IReadOnlyList<string> SliceValues);

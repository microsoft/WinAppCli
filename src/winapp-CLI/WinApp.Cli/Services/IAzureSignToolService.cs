// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Runs signtool with the Azure Trusted Signing dlib to code-sign a file.
/// Owns acquisition of the Trusted Signing client package and signtool invocation,
/// keeping that logic out of the command handler so it can be substituted in tests.
/// </summary>
internal interface IAzureSignToolService
{
    /// <summary>
    /// Signs <paramref name="filePath"/> using the supplied Trusted Signing metadata file.
    /// </summary>
    /// <param name="filePath">The file to sign (exe, msix, or msixbundle).</param>
    /// <param name="metadataFilePath">The Trusted Signing metadata.json file.</param>
    /// <param name="tenantId">Azure tenant ID to forward to signtool's dlib, if known.</param>
    /// <param name="taskContext">Status/progress context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">When signing fails.</exception>
    Task SignAsync(
        FileInfo filePath,
        FileInfo metadataFilePath,
        string? tenantId,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);
}

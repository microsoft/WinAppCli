// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

public record AddExecutionAliasOptions(
    FileInfo ManifestFile,
    string? AliasName,
    string? AppId);

public record ManifestGenerationInfo(
    string PackageName,
    string PublisherName,
    string Version,
    string Description);

public record SparseInitResult(
    FileInfo ManifestPath,
    ManifestGenerationInfo Info,
    DirectoryInfo AssetsDirectory);

internal interface IManifestService
{
    public Task<ManifestGenerationInfo> PromptForManifestInfoAsync(
        DirectoryInfo directory,
        string? packageName,
        string? publisherName,
        string version,
        string? description,
        string? executable,
        bool useDefaults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a sparse identity <c>appxmanifest.xml</c> (plus placeholder assets) for an
    /// existing desktop executable. Infers metadata defaults from the exe via
    /// <see cref="System.Diagnostics.FileVersionInfo"/>, optionally prompting for overrides.
    /// The generated manifest references the external exe by name so it can be packed as an
    /// identity-only MSIX with <c>winapp pack</c>.
    /// </summary>
    public Task<SparseInitResult> GenerateSparseIdentityManifestAsync(
        DirectoryInfo outputDirectory,
        FileInfo executable,
        string? packageName,
        string? publisherName,
        bool useDefaults,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    public Task GenerateManifestAsync(
        DirectoryInfo directory,
        ManifestGenerationInfo manifestGenerationInfo,
        ManifestTemplates manifestTemplate,
        FileInfo? logoPath,
        string? executable,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    public Task UpdateManifestAssetsAsync(
        FileInfo manifestPath,
        FileInfo imagePath,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default);

    public Task<AddExecutionAliasResult> AddExecutionAliasAsync(
        AddExecutionAliasOptions options,
        CancellationToken cancellationToken = default);
}

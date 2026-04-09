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

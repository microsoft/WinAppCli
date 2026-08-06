// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IManifestTemplateService
{
    Task GenerateCompleteManifestAsync(
        DirectoryInfo outputDirectory,
        string packageName,
        string publisherName,
        string version,
        ManifestTemplates manifestTemplate,
        string description,
        TaskContext taskContext,
        string manifestFileName = "Package.appxmanifest",
        string? executableName = null,
        CancellationToken cancellationToken = default);
}

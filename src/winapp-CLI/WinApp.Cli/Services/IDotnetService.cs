// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IDotnetService
{
    /// <summary>
    /// Returns true if the dotnet SDK is available on PATH.
    /// </summary>
    Task<bool> IsDotnetInstalledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns true if VijayAnand.WinUITemplates is already installed.
    /// </summary>
    Task<bool> IsWinUITemplatesInstalledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Installs the VijayAnand.WinUITemplates NuGet template package.
    /// </summary>
    Task<(int exitCode, string output)> InstallWinUITemplatesAsync(
        TaskContext taskContext, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>dotnet new &lt;templateShortName&gt;</c> with the provided arguments.
    /// </summary>
    Task<(int exitCode, string output)> RunDotnetNewAsync(
        string templateShortName, string? name, string? outputDir,
        Dictionary<string, string>? parameters,
        TaskContext taskContext, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>dotnet restore</c> in the given project directory.
    /// </summary>
    Task<(int exitCode, string output)> RunDotnetRestoreAsync(
        string projectDir, TaskContext taskContext, CancellationToken cancellationToken);
}

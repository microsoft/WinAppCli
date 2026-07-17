// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Drives project-mode <c>winapp run</c>: classifies the input (folder vs project), verifies the
/// .NET SDK is capable, and builds + resolves the MSBuild output properties needed to launch a
/// packaged or unpackaged WinUI app. See spec <c>specs/winapp-run-csproj.md</c> §6–§8.
/// </summary>
internal interface IProjectRunService
{
    /// <summary>
    /// Classifies the run input into folder mode (existing behavior) or project mode.
    /// </summary>
    /// <param name="input">The positional argument: a <c>.csproj</c> file or a directory.</param>
    /// <returns>The resolved mode + project file (when project mode).</returns>
    /// <remarks>
    /// When a directory contains multiple <c>.csproj</c> files, each candidate is classified via a
    /// lightweight MSBuild evaluation (<c>--getProperty:OutputType,IsTestProject</c>) so properties
    /// contributed by imports (e.g. <c>Directory.Build.props</c>, the test SDK) are honored, not just
    /// inline XML. Evaluation falls back to a static parse when the SDK/restore is unavailable.
    /// Folder mode (a directory with no <c>.csproj</c>) never evaluates, keeping its behavior identical.
    /// </remarks>
    /// <exception cref="ProjectRunException">
    /// Thrown when the input is an unsupported file type, or a directory contains multiple candidate
    /// <c>.csproj</c> files and the intended one is ambiguous.
    /// </exception>
    Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies that a capable .NET SDK (≥ 8.0.100, which supports <c>--getProperty</c>) is available.
    /// </summary>
    /// <returns>An actionable error message if the SDK is missing/too old, otherwise <c>null</c>.</returns>
    Task<string?> CheckSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Builds the project (unless <see cref="ProjectRunOptions.NoBuild"/>) and resolves the evaluated
    /// MSBuild output properties, returning the packaging determination and launch paths.
    /// </summary>
    /// <exception cref="ProjectRunException">Thrown on a guardrail violation (e.g. a non-executable project).</exception>
    Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// A user-facing project-mode error (misconfiguration or ambiguous input). The <c>run</c> handler
/// catches it, prints the message, and returns exit code 1.
/// </summary>
internal sealed class ProjectRunException(string message) : Exception(message);

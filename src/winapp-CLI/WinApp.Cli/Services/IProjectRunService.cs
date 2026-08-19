// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Drives project-mode <c>winapp run</c>: classifies the input (folder vs project), verifies the
/// .NET SDK is capable for modern projects, and builds + resolves the MSBuild output properties
/// needed to launch a packaged or unpackaged WinUI app. Classic UWP projects are routed to
/// <see cref="ILegacyUwpRunService"/> before the .NET SDK gate.
/// </summary>
internal interface IProjectRunService
{
    /// <summary>
    /// Classifies the run input into folder mode (existing behavior) or project mode.
    /// </summary>
    /// <param name="input">The positional argument: a <c>.csproj</c>/<c>.sln</c>/<c>.slnx</c> file or a directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="projectSelector">
    /// Optional <c>--project</c> selector used to pick the runnable project when the input is a
    /// solution (or a directory with several candidate <c>.csproj</c> files) that exposes more than
    /// one launchable app project. Matched by project name (with or without the <c>.csproj</c>
    /// extension) or full path. Ignored when the target is unambiguous.
    /// </param>
    /// <param name="classificationInputs">
    /// Optional effective build inputs (Configuration, architecture, TargetFramework, user <c>-p</c>)
    /// threaded into candidate classification so a project whose <c>OutputType</c>/test markers are
    /// conditional on those globals is classified the way it will build. Null classifies against
    /// MSBuild defaults (prior behavior). Only consulted when the input is a solution or a directory
    /// with multiple <c>.csproj</c> candidates; ignored on unambiguous targets.
    /// </param>
    /// <returns>The resolved mode + project file (when project mode).</returns>
    /// <remarks>
    /// A <c>.sln</c>/<c>.slnx</c> file (or a directory containing exactly one) resolves to project
    /// mode against its single runnable app project, and the resolution records the solution so the
    /// build defines <c>$(SolutionDir)</c>. When a directory contains multiple <c>.csproj</c> files,
    /// each candidate is classified via a lightweight MSBuild evaluation
    /// (<c>--getProperty:OutputType,IsTestProject</c>) so properties contributed by imports (e.g.
    /// <c>Directory.Build.props</c>, the test SDK) are honored, not just inline XML. Evaluation falls
    /// back to a static parse when the SDK/restore is unavailable. Folder mode (a directory with no
    /// <c>.csproj</c>/solution) never evaluates, keeping its behavior identical.
    /// </remarks>
    /// <exception cref="ProjectRunException">
    /// Thrown when the input is an unsupported file type, or a solution/directory exposes multiple
    /// runnable candidates and the intended one is ambiguous (and no matching selector was given).
    /// </exception>
    Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken, string? projectSelector = null, ProjectClassificationInputs? classificationInputs = null);

    /// <summary>
    /// Verifies that a capable .NET SDK (≥ 8.0.100, which supports <c>--getProperty</c>) is available.
    /// This floor also covers <c>.slnx</c> input: its project list is parsed directly from the XML rather
    /// than via <c>dotnet sln list</c> (which understands <c>.slnx</c> only on SDK 9.0.200+), and the
    /// resolved <c>.csproj</c> — not the solution — is what gets built, so no <c>.slnx</c>-aware SDK is
    /// required.
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

    /// <summary>
    /// Cheap, side-effect-free probe (no build) that reports whether the project is DEFINITIVELY
    /// unpackaged — i.e. it declares an explicit <c>WindowsPackageType=None</c>. Used by the run
    /// handler to fail fast on identity-only options (e.g. <c>--no-launch</c>) that are meaningless
    /// for unpackaged apps BEFORE paying the full build cost (issue #676), rather than only rejecting
    /// them in the authoritative post-build gate.
    /// </summary>
    /// <returns>
    /// <c>true</c> only when <c>WindowsPackageType</c> evaluates to <c>None</c>. Returns <c>false</c>
    /// for packaged apps AND for the indeterminate cases (unset property, or the evaluation failed) —
    /// those fall through to the normal build + authoritative packaging determination, which can
    /// still classify an app as unpackaged via post-build signals (e.g. an emitted recipe). Never
    /// throws or builds; a failed evaluation is treated as indeterminate.
    /// </returns>
    Task<bool> IsDefinitivelyUnpackagedAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// A user-facing project-mode error (misconfiguration or ambiguous input). The <c>run</c> handler
/// catches it, prints the message, and returns exit code 1.
/// </summary>
internal sealed class ProjectRunException(string message) : Exception(message);

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Whether <c>winapp run</c> operates on a pre-built output folder (folder mode, unchanged)
/// or builds a <c>.csproj</c> from source (project mode).
/// </summary>
internal enum WinAppRunMode
{
    /// <summary>Input is a build-output folder — the existing, unchanged behavior.</summary>
    Folder,

    /// <summary>Input is a <c>.csproj</c> (or a directory containing exactly one buildable one).</summary>
    Project,
}

/// <summary>
/// The effective packaging of a project-mode target, derived from the evaluated
/// <c>WindowsPackageType</c> MSBuild property (never from manifest presence).
/// </summary>
internal enum ProjectPackaging
{
    /// <summary>MSIX identity (loose-layout register + AUMID launch).</summary>
    Packaged,

    /// <summary>No identity (<c>WindowsPackageType=None</c>) — launch the apphost <c>.exe</c> directly.</summary>
    Unpackaged,
}

/// <summary>
/// The outcome of resolving a <c>.csproj</c> input to a concrete run mode.
/// Either <see cref="Mode"/> is <see cref="WinAppRunMode.Folder"/> (fall back to the existing
/// folder-mode path) or it is <see cref="WinAppRunMode.Project"/> and <see cref="Csproj"/> is set.
/// </summary>
/// <param name="Mode">The resolved run mode.</param>
/// <param name="Csproj">The resolved project file, when <see cref="Mode"/> is Project.</param>
/// <param name="ProjectDirectory">The directory containing the project (project mode) or the input folder.</param>
/// <param name="Solution">The solution the project was resolved from (defines <c>$(SolutionDir)</c> and siblings for the build/evaluate passes); null for a bare <c>.csproj</c>/directory input.</param>
/// <param name="SelectionReason">Why this project was chosen for an ambiguous input (shown in the context line); null if unambiguous.</param>
internal sealed record RunInputResolution(
    WinAppRunMode Mode,
    FileInfo? Csproj,
    DirectoryInfo ProjectDirectory,
    FileInfo? Solution = null,
    string? SelectionReason = null);

/// <summary>
/// The build-and-resolve result for a project-mode target: the evaluated output
/// paths plus the packaging determination used to pick the launch strategy.
/// </summary>
/// <param name="Csproj">The project that was built/evaluated.</param>
/// <param name="TargetDir">Absolute output directory (contains the manifest/recipe for packaged apps).</param>
/// <param name="RunCommand">Absolute, runnable apphost <c>.exe</c> for the unpackaged direct launch (null if not produced).</param>
/// <param name="Packaging">Packaged vs unpackaged.</param>
/// <param name="SelfContained">True when <c>WindowsAppSDKSelfContained=true</c> — the runtime install is skipped.</param>
/// <param name="Architecture">The resolved app architecture (x64 / arm64 / x86) used for build + runtime install.</param>
/// <param name="Framework">The effective target framework the app was built for (mirrors <c>ProjectRunOptions.Framework</c>); null for a single-targeted project. Threaded into the unpackaged runtime install so the version resolves from the built TFM.</param>
/// <param name="NoRestore">Mirrors <c>ProjectRunOptions.NoRestore</c>; threaded into the unpackaged <c>dotnet list package</c> discovery so it can't trigger a restore the user skipped.</param>
internal sealed record ProjectRunResolution(
    FileInfo Csproj,
    string TargetDir,
    string? RunCommand,
    ProjectPackaging Packaging,
    bool SelfContained,
    string Architecture,
    string? Framework = null,
    bool NoRestore = false);

/// <summary>
/// User-provided build inputs for project mode, forwarded to <c>dotnet build</c> / <c>dotnet msbuild</c>.
/// </summary>
/// <param name="Configuration">Build configuration (default <c>Debug</c>).</param>
/// <param name="Architecture">Canonical target arch (<c>x64</c> / <c>arm64</c> / <c>x86</c>).</param>
/// <param name="Framework">Optional target framework moniker for multi-targeted projects.</param>
/// <param name="NoBuild">Skip the build and evaluate the existing output only.</param>
/// <param name="NoRestore">Pass <c>--no-restore</c> to the build.</param>
/// <param name="Properties">Raw repeatable <c>-p Name=Value</c> passthrough, forwarded to both build and evaluation.</param>
/// <param name="Json">When true, suppress human-readable stdout (banner) and route build diagnostics to stderr so stdout stays pure JSON.</param>
/// <param name="Solution">The solution the target was resolved from; when set, the build/evaluate passes define <c>$(SolutionDir)</c> and sibling <c>Solution*</c> properties so referencing projects build as they do in VS. Null for a bare <c>.csproj</c>.</param>
internal sealed record ProjectRunOptions(
    string Configuration,
    string Architecture,
    string? Framework,
    bool NoBuild,
    bool NoRestore,
    IReadOnlyList<string> Properties,
    bool Json = false,
    FileInfo? Solution = null);

/// <summary>
/// The effective build inputs used to classify runnable candidates (multi-<c>.csproj</c> directory or
/// solution) under the SAME MSBuild globals the build/evaluate passes use, so a project whose
/// <c>OutputType</c>/test markers are conditional is classified the way it will actually build. Null
/// classifies against MSBuild defaults (pre-existing behavior).
/// </summary>
/// <param name="Configuration">The effective <c>-c</c> Configuration (e.g. <c>Debug</c>/<c>Release</c>).</param>
/// <param name="Architecture">The resolved target architecture, mapped to a RID for the evaluate.</param>
/// <param name="Framework">The explicit <c>--framework</c> TargetFramework, or null when unset.</param>
/// <param name="Properties">The raw user <c>-p Name=Value</c> properties (dedicated-switch dupes filtered).</param>
internal sealed record ProjectClassificationInputs(
    string Configuration,
    string Architecture,
    string? Framework,
    IReadOnlyList<string> Properties);

/// <summary>
/// Outcome of <see cref="Services.IProjectRunService.BuildAndResolveAsync"/>. On success,
/// <see cref="Resolution"/> is set and <see cref="ExitCode"/> is 0. On a build failure, the dotnet
/// errors have already been surfaced and <see cref="ExitCode"/> is the non-zero dotnet exit code.
/// </summary>
internal sealed record ProjectBuildOutcome(ProjectRunResolution? Resolution, int ExitCode);

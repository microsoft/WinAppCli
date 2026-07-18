// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Service for detecting compatible projects in a directory tree.
/// </summary>
internal interface IProjectDetectionService
{
    /// <summary>
    /// Performs a breadth-first search of the directory tree to find compatible projects.
    /// </summary>
    /// <param name="root">The root directory to start searching from</param>
    /// <param name="maxProjects">Maximum number of projects to find before stopping</param>
    /// <param name="progress">Optional progress callback invoked as each project is discovered</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of detected projects in BFS discovery order</returns>
    Task<IReadOnlyList<DetectedProject>> DetectProjectsAsync(
        DirectoryInfo root,
        int maxProjects,
        IProgress<DetectedProject>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks a single directory for a compatible project marker.
    /// </summary>
    /// <param name="directory">The directory to check</param>
    /// <returns>The detected project, or null if no project is found</returns>
    DetectedProject? DetectProjectAt(DirectoryInfo directory);

    /// <summary>
    /// Classifies a candidate <c>.csproj</c> as a runnable non-test executable, preferring an MSBuild
    /// evaluation of <c>OutputType</c>/<c>IsTestProject</c> (which honors imports such as SDK defaults,
    /// <c>Directory.Build.props</c>, or the test SDK) and falling back to the static XML parse of
    /// <see cref="DetectProjectAt"/>'s underlying logic when evaluation is unavailable (no capable SDK,
    /// project not restored). This is the shared owner of the "runnable app project" rule used by both
    /// directory detection and <c>winapp run</c> project/solution resolution.
    /// </summary>
    /// <param name="csproj">The project file to classify.</param>
    /// <param name="workingDirectory">Directory the evaluation runs in (the input or solution directory).</param>
    /// <param name="extraMsbuildProperties">
    /// Optional additional MSBuild tokens (e.g. <c>-p:SolutionDir=…</c>) so evaluation sees the same
    /// solution-defined properties the build will, avoiding misclassification of projects that depend
    /// on <c>$(SolutionDir)</c>. Pass null for a bare project with no solution context.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the project is an executable (<c>Exe</c>/<c>WinExe</c>) non-test project.</returns>
    Task<bool> IsExecutableNonTestProjectAsync(
        FileInfo csproj,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string>? extraMsbuildProperties,
        CancellationToken cancellationToken);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// How a candidate <c>.csproj</c> relates to <c>winapp run</c>'s notion of a launch target.
/// </summary>
internal enum ProjectRunnability
{
    /// <summary>Not an executable (e.g. a <c>Library</c> or source generator) — never a launch target.</summary>
    NotRunnable,

    /// <summary>An executable (<c>Exe</c>/<c>WinExe</c>) that is not a test project — the preferred launch target.</summary>
    App,

    /// <summary>
    /// An executable that is a test project — detected via <c>IsTestProject</c>, the VS
    /// <c>TestContainer</c> project capability, or a test-framework package reference. WinUI MSTest
    /// projects are themselves packaged <c>WinExe</c> apps that omit <c>IsTestProject</c>, so
    /// <c>OutputType</c> alone cannot distinguish them from the real app. Launched only when explicitly
    /// selected or when it is the sole runnable project, so a test host never shadows a real app during
    /// auto-selection.
    /// </summary>
    Test,
}

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
    /// Classifies a candidate <c>.csproj</c> as a runnable app, a runnable test project, or not
    /// runnable, preferring an MSBuild evaluation of <c>OutputType</c>/<c>IsTestProject</c> plus the
    /// <c>ProjectCapability</c>/<c>PackageReference</c> items (which honor imports such as SDK defaults,
    /// <c>Directory.Build.props</c>, or the test SDK) and falling back to the static XML parse of
    /// <see cref="DetectProjectAt"/>'s underlying logic when evaluation is unavailable (no capable SDK,
    /// project not restored). This is the shared owner of the "runnable project" rule used by both
    /// directory detection and <c>winapp run</c> project/solution resolution. Test projects are
    /// distinguished so auto-selection can prefer a real app yet still run a lone test project.
    /// </summary>
    /// <param name="csproj">The project file to classify.</param>
    /// <param name="workingDirectory">Directory the evaluation runs in (the input or solution directory).</param>
    /// <param name="extraMsbuildProperties">
    /// Optional additional MSBuild tokens (e.g. <c>-p:SolutionDir=…</c>) so evaluation sees the same
    /// solution-defined properties the build will, avoiding misclassification of projects that depend
    /// on <c>$(SolutionDir)</c>. Pass null for a bare project with no solution context.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The project's <see cref="ProjectRunnability"/> classification.</returns>
    Task<ProjectRunnability> ClassifyRunnableAsync(
        FileInfo csproj,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string>? extraMsbuildProperties,
        CancellationToken cancellationToken);
}

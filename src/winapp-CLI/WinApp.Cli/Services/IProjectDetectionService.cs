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
    /// <param name="searchAll">When true, skips the default ignore list for directory names</param>
    /// <param name="progress">Optional progress callback invoked as each project is discovered</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of detected projects in BFS discovery order</returns>
    Task<IReadOnlyList<DetectedProject>> DetectProjectsAsync(
        DirectoryInfo root,
        int maxProjects,
        bool searchAll,
        IProgress<DetectedProject>? progress,
        CancellationToken cancellationToken);
}

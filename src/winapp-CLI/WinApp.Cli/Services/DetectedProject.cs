// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Represents a project detected during directory scanning.
/// </summary>
/// <param name="Type">The detected project type</param>
/// <param name="Directory">The root directory of the detected project</param>
/// <param name="DisplayPath">Relative path from the search root for display purposes</param>
internal sealed record DetectedProject(DetectedProjectType Type, DirectoryInfo Directory, string DisplayPath)
{
    /// <summary>
    /// Returns a human-readable description like "Tauri project at src/my-app"
    /// </summary>
    public string ToDisplayString() => $"{TypeLabel} project at {DisplayPath}";

    /// <summary>
    /// Returns the user-friendly label for the project type.
    /// </summary>
    public string TypeLabel => Type switch
    {
        DetectedProjectType.Tauri => "Tauri",
        DetectedProjectType.Electron => "Electron",
        DetectedProjectType.Flutter => "Flutter",
        DetectedProjectType.Dotnet => ".NET",
        DetectedProjectType.Rust => "Rust",
        DetectedProjectType.CPP => "C++",
        _ => Type.ToString()
    };
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;

namespace WinApp.Cli.Services;

// JS package manager detected from a workspace.
public sealed record DetectedPackageManager(string Name, string InstallCommand);

// Precedence: Corepack `packageManager` field → lockfile → npm.
public interface IPackageManagerDetector
{
    // Never null — falls back to npm.
    DetectedPackageManager Detect(DirectoryInfo workspaceDirectory);
}

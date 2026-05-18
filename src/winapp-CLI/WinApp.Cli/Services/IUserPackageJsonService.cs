// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;

namespace WinApp.Cli.Services;

public enum RuntimeDependencyOutcome
{
    Added,
    AlreadyPresent,
    PresentInDevDependencies,
    NoPackageJson,
}

// Edits the user's package.json. Bindings need `dependencies` (not dev),
// or `npm ci --omit=dev` strips them.
public interface IUserPackageJsonService
{
    RuntimeDependencyOutcome EnsureRuntimeDependency(
        DirectoryInfo workspaceDirectory,
        string packageName,
        string version);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IWindowsAppRuntimeDeploymentService
{
    Task<WindowsAppRuntimePrepareResult> PrepareAsync(
        string version,
        string architecture,
        DirectoryInfo outputDirectory,
        bool install,
        TaskContext taskContext,
        CancellationToken cancellationToken);
}

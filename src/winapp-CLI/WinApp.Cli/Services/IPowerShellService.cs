// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IPowerShellService
{
    public Task<(int exitCode, string output, string error)> RunCommandAsync(
        string command,
        TaskContext taskContext,
        bool elevated = false,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default);
}

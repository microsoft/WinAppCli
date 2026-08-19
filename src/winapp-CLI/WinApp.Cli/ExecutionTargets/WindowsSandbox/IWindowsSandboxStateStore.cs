// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal interface IWindowsSandboxStateStore
{
    FileInfo GetStateFile();

    Task<WindowsSandboxStateReadResult> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(WindowsSandboxTargetState state, CancellationToken cancellationToken = default);
}

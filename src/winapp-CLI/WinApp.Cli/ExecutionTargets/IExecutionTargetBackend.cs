// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets;

internal interface IExecutionTargetBackend
{
    ExecutionTargetRef Target { get; }

    Task<ExecutionTargetProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    Task<ExecutionTargetStatusResult> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<ExecutionTargetEnsureResult> EnsureAsync(
        ExecutionTargetRequirements requirements,
        CancellationToken cancellationToken = default);
}

internal interface IExecutionTargetService
{
    Task<ExecutionTargetProbeResult> ProbeAsync(
        ExecutionTargetRef target,
        CancellationToken cancellationToken = default);

    Task<ExecutionTargetStatusResult> GetStatusAsync(
        ExecutionTargetRef target,
        CancellationToken cancellationToken = default);

    Task<ExecutionTargetEnsureResult> EnsureAsync(
        ExecutionTargetRef target,
        ExecutionTargetRequirements requirements,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets;

internal sealed class ExecutionTargetService(IExecutionTargetBackend backend) : IExecutionTargetService
{
    public Task<ExecutionTargetProbeResult> ProbeAsync(
        ExecutionTargetRef target,
        CancellationToken cancellationToken = default)
    {
        if (target != backend.Target)
        {
            return Task.FromResult(new ExecutionTargetProbeResult(false, [BackendMismatch(target)]));
        }

        return backend.ProbeAsync(cancellationToken);
    }

    public Task<ExecutionTargetStatusResult> GetStatusAsync(
        ExecutionTargetRef target,
        CancellationToken cancellationToken = default)
    {
        if (target != backend.Target)
        {
            return Task.FromResult(new ExecutionTargetStatusResult(
                target,
                ExecutionTargetStatus.Unavailable,
                null,
                null,
                ExecutionTargetCapabilities.Stopped,
                [BackendMismatch(target)]));
        }

        return backend.GetStatusAsync(cancellationToken);
    }

    public async Task<ExecutionTargetEnsureResult> EnsureAsync(
        ExecutionTargetRef target,
        ExecutionTargetRequirements requirements,
        CancellationToken cancellationToken = default)
    {
        if (target != backend.Target)
        {
            return ExecutionTargetEnsureResult.Failure(BackendMismatch(target));
        }

        var result = await backend.EnsureAsync(requirements, cancellationToken);
        if (result.Instance is not null && !result.Instance.Capabilities.Satisfies(requirements))
        {
            return ExecutionTargetEnsureResult.Failure(new ExecutionTargetDiagnostic(
                ExecutionTargetDiagnosticCode.CapabilityUnavailable,
                $"Execution target '{target.Id}' does not satisfy the requested capabilities."));
        }

        return result;
    }

    private ExecutionTargetDiagnostic BackendMismatch(ExecutionTargetRef requested) =>
        new(
            ExecutionTargetDiagnosticCode.BackendMismatch,
            $"Execution target backend '{backend.Target.Id}' cannot handle '{requested.Id}'.");
}

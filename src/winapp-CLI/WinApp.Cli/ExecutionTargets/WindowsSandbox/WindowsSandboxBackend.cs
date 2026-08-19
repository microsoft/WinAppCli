// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal sealed class WindowsSandboxBackend(
    IWindowsSandboxHost host,
    IWindowsSandboxCli cli,
    IWindowsSandboxStateStore stateStore,
    IWindowsSandboxMutationLock mutationLock) : IExecutionTargetBackend
{
    private static readonly ExecutionTargetCapabilities RunningCapabilities =
        new(true, false, false, false, false, false);

    public ExecutionTargetRef Target => ExecutionTargetRef.WindowsSandboxDefault;

    public async Task<ExecutionTargetProbeResult> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!host.IsSupportedOperatingSystem)
        {
            return new ExecutionTargetProbeResult(false, [UnsupportedHost()]);
        }

        var list = await cli.ListAsync(cancellationToken);
        if (!list.Succeeded)
        {
            return new ExecutionTargetProbeResult(false, [CliDiagnostic(list)]);
        }

        return ExecutionTargetProbeResult.Supported;
    }

    public async Task<ExecutionTargetStatusResult> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (!host.IsSupportedOperatingSystem)
        {
            return UnavailableStatus(UnsupportedHost());
        }

        var list = await cli.ListAsync(cancellationToken);
        if (!list.Succeeded)
        {
            return UnavailableStatus(CliDiagnostic(list));
        }

        var state = await stateStore.ReadAsync(cancellationToken);
        return ReconcileStatus(list.Value!, state);
    }

    public Task<ExecutionTargetEnsureResult> EnsureAsync(
        ExecutionTargetRequirements requirements,
        CancellationToken cancellationToken = default)
    {
        if (requirements.InteractiveDesktop)
        {
            return Task.FromResult(ExecutionTargetEnsureResult.Failure(new ExecutionTargetDiagnostic(
                ExecutionTargetDiagnosticCode.CapabilityUnavailable,
                "The Windows Sandbox foundation does not establish an interactive guest session.")));
        }

        return Task.Run(() => EnsureUnderLock(cancellationToken), cancellationToken);
    }

    private ExecutionTargetEnsureResult EnsureUnderLock(CancellationToken cancellationToken)
    {
        using var lease = mutationLock.Acquire(cancellationToken);

        if (!host.IsSupportedOperatingSystem)
        {
            return ExecutionTargetEnsureResult.Failure(UnsupportedHost());
        }

        var list = cli.ListAsync(cancellationToken).GetAwaiter().GetResult();
        if (!list.Succeeded)
        {
            return ExecutionTargetEnsureResult.Failure(CliDiagnostic(list));
        }

        var stateRead = stateStore.ReadAsync(cancellationToken).GetAwaiter().GetResult();
        var runningIds = list.Value!;
        if (runningIds.Count > 0)
        {
            return EnsureExisting(runningIds, stateRead);
        }

        if (stateRead.Status is WindowsSandboxStateReadStatus.UnsupportedVersion
            or WindowsSandboxStateReadStatus.UnsafePath)
        {
            return ExecutionTargetEnsureResult.Failure(StateDiagnostic(stateRead));
        }

        // Without a returned ID, a post-cancellation singleton cannot be distinguished
        // from an externally started Sandbox and must not be stopped automatically.
        var start = cli.StartAsync(cancellationToken).GetAwaiter().GetResult();
        if (!start.Succeeded)
        {
            return ExecutionTargetEnsureResult.Failure(StartDiagnostic(start));
        }

        var startedId = start.Value!;
        try
        {
            var confirmation = cli.ListAsync(cancellationToken).GetAwaiter().GetResult();
            if (!confirmation.Succeeded ||
                confirmation.Value!.Count != 1 ||
                !string.Equals(confirmation.Value[0], startedId, StringComparison.OrdinalIgnoreCase))
            {
                var detail = confirmation.Succeeded
                    ? "The newly started Windows Sandbox could not be confirmed as the only running instance."
                    : confirmation.Error!;
                return FailureWithRollback(new ExecutionTargetDiagnostic(
                    ExecutionTargetDiagnosticCode.WindowsSandboxStartFailed,
                    detail,
                    RecoveryCommand(startedId)),
                    startedId);
            }

            var epoch = ExecutionTargetEpoch.Create();
            var previousRevision = stateRead.State?.Revision ?? 0;
            var state = new WindowsSandboxTargetState
            {
                ProviderInstanceId = startedId,
                Epoch = epoch.Value,
                Revision = checked(previousRevision + 1),
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };

            try
            {
                stateStore.WriteAsync(state, cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return FailureWithRollback(new ExecutionTargetDiagnostic(
                    ExecutionTargetDiagnosticCode.WindowsSandboxStateUnavailable,
                    $"Failed to commit Windows Sandbox ownership state: {ex.Message}",
                    RecoveryCommand(startedId)),
                    startedId);
            }

            return Success(startedId, epoch);
        }
        catch (OperationCanceledException)
        {
            _ = Rollback(startedId);
            throw;
        }
    }

    private ExecutionTargetEnsureResult EnsureExisting(
        IReadOnlyList<string> runningIds,
        WindowsSandboxStateReadResult stateRead)
    {
        if (stateRead.Status is WindowsSandboxStateReadStatus.Corrupt
            or WindowsSandboxStateReadStatus.UnsupportedVersion
            or WindowsSandboxStateReadStatus.UnsafePath)
        {
            return ExecutionTargetEnsureResult.Failure(StateDiagnostic(stateRead));
        }

        if (runningIds.Count == 1 &&
            stateRead.Status == WindowsSandboxStateReadStatus.Valid &&
            string.Equals(
                runningIds[0],
                stateRead.State!.ProviderInstanceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Success(
                runningIds[0],
                new ExecutionTargetEpoch(stateRead.State.Epoch));
        }

        return ExecutionTargetEnsureResult.Failure(UnmanagedDiagnostic(runningIds));
    }

    private ExecutionTargetStatusResult ReconcileStatus(
        IReadOnlyList<string> runningIds,
        WindowsSandboxStateReadResult stateRead)
    {
        if (runningIds.Count == 0)
        {
            if (stateRead.Status is WindowsSandboxStateReadStatus.UnsupportedVersion
                or WindowsSandboxStateReadStatus.UnsafePath)
            {
                return UnavailableStatus(StateDiagnostic(stateRead));
            }

            return new ExecutionTargetStatusResult(
                Target,
                ExecutionTargetStatus.Stopped,
                null,
                null,
                ExecutionTargetCapabilities.Stopped,
                []);
        }

        if (stateRead.Status is WindowsSandboxStateReadStatus.Corrupt
            or WindowsSandboxStateReadStatus.UnsupportedVersion
            or WindowsSandboxStateReadStatus.UnsafePath)
        {
            return UnavailableStatus(StateDiagnostic(stateRead));
        }

        if (runningIds.Count == 1 &&
            stateRead.Status == WindowsSandboxStateReadStatus.Valid &&
            string.Equals(
                runningIds[0],
                stateRead.State!.ProviderInstanceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutionTargetStatusResult(
                Target,
                ExecutionTargetStatus.Running,
                runningIds[0],
                new ExecutionTargetEpoch(stateRead.State.Epoch),
                RunningCapabilities,
                []);
        }

        var diagnostic = UnmanagedDiagnostic(runningIds);
        return new ExecutionTargetStatusResult(
            Target,
            ExecutionTargetStatus.Unmanaged,
            null,
            null,
            ExecutionTargetCapabilities.Stopped,
            [diagnostic]);
    }

    private ExecutionTargetEnsureResult FailureWithRollback(
        ExecutionTargetDiagnostic primary,
        string instanceId)
    {
        var rollbackDiagnostic = Rollback(instanceId);
        return rollbackDiagnostic is null
            ? ExecutionTargetEnsureResult.Failure(primary)
            : ExecutionTargetEnsureResult.Failure(primary, rollbackDiagnostic);
    }

    private ExecutionTargetDiagnostic? Rollback(string instanceId)
    {
        var result = cli.StopAsync(instanceId, CancellationToken.None).GetAwaiter().GetResult();
        return result.Succeeded
            ? null
            : new ExecutionTargetDiagnostic(
                ExecutionTargetDiagnosticCode.WindowsSandboxRollbackFailed,
                $"Failed to stop newly created Windows Sandbox instance {instanceId}: {result.Error}",
                RecoveryCommand(instanceId));
    }

    private ExecutionTargetEnsureResult Success(string instanceId, ExecutionTargetEpoch epoch) =>
        new(
            new ExecutionTargetInstance(Target, instanceId, epoch, RunningCapabilities),
            []);

    private ExecutionTargetStatusResult UnavailableStatus(ExecutionTargetDiagnostic diagnostic) =>
        new(
            Target,
            ExecutionTargetStatus.Unavailable,
            null,
            null,
            ExecutionTargetCapabilities.Stopped,
            [diagnostic]);

    private static ExecutionTargetDiagnostic UnsupportedHost() =>
        new(
            ExecutionTargetDiagnosticCode.UnsupportedHost,
            "Windows Sandbox execution targets require Windows 11 version 24H2 (build 26100) or newer.");

    private static ExecutionTargetDiagnostic CliDiagnostic<T>(WindowsSandboxCliResult<T> result)
    {
        var code = result.Failure switch
        {
            WindowsSandboxCliFailure.ExecutableMissing =>
                ExecutionTargetDiagnosticCode.WindowsSandboxCliMissing,
            WindowsSandboxCliFailure.IncompatibleOutput =>
                ExecutionTargetDiagnosticCode.WindowsSandboxCliIncompatible,
            _ => ExecutionTargetDiagnosticCode.WindowsSandboxListFailed,
        };
        return new ExecutionTargetDiagnostic(code, result.Error!);
    }

    private static ExecutionTargetDiagnostic StateDiagnostic(WindowsSandboxStateReadResult state) =>
        new(
            ExecutionTargetDiagnosticCode.WindowsSandboxStateUnavailable,
            state.Error ?? "Windows Sandbox ownership state is unavailable.");

    private static ExecutionTargetDiagnostic StartDiagnostic(WindowsSandboxCliResult<string> result)
    {
        if (result.Failure is WindowsSandboxCliFailure.ExecutableMissing
            or WindowsSandboxCliFailure.IncompatibleOutput)
        {
            return CliDiagnostic(result);
        }

        return new ExecutionTargetDiagnostic(
            ExecutionTargetDiagnosticCode.WindowsSandboxStartFailed,
            result.Error!);
    }

    private static ExecutionTargetDiagnostic UnmanagedDiagnostic(IReadOnlyList<string> runningIds)
    {
        var ids = string.Join(", ", runningIds);
        var recovery = runningIds.Count == 1 ? RecoveryCommand(runningIds[0]) : null;
        return new ExecutionTargetDiagnostic(
            ExecutionTargetDiagnosticCode.WindowsSandboxUnmanagedInstance,
            $"Windows Sandbox instance(s) {ids} are running but are not provably owned by winapp.",
            recovery);
    }

    private static string RecoveryCommand(string instanceId) =>
        $"wsb stop --id {instanceId}";
}

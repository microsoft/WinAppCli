// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal enum WindowsSandboxCliFailure
{
    ExecutableMissing,
    CommandFailed,
    IncompatibleOutput,
}

internal sealed record WindowsSandboxCliResult<T>(
    T? Value,
    WindowsSandboxCliFailure? Failure,
    string? Error)
{
    public bool Succeeded => Failure is null;

    public static WindowsSandboxCliResult<T> Success(T value) => new(value, null, null);

    public static WindowsSandboxCliResult<T> Failed(
        WindowsSandboxCliFailure failure,
        string error) => new(default, failure, error);
}

internal interface IWindowsSandboxCli
{
    Task<WindowsSandboxCliResult<IReadOnlyList<string>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<WindowsSandboxCliResult<string>> StartAsync(
        CancellationToken cancellationToken = default);

    Task<WindowsSandboxCliResult<bool>> StopAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

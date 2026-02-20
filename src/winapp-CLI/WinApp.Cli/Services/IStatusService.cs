// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IStatusService
{
    public Task<int> ExecuteWithStatusAsync<T>(string inProgressMessage, Func<TaskContext, CancellationToken, Task<(int ReturnCode, T CompletedMessage)>> taskFunc, CancellationToken cancellationToken);

    /// <summary>
    /// Execute a task with a quiet TaskContext that suppresses all console output.
    /// Use this in --json mode to run service methods that require a TaskContext
    /// without polluting stdout.
    /// </summary>
    public Task<TResult> ExecuteQuietlyAsync<TResult>(Func<TaskContext, CancellationToken, Task<TResult>> taskFunc, CancellationToken cancellationToken);
}

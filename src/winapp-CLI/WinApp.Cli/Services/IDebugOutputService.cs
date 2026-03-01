// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IDebugOutputService
{
    /// <summary>
    /// Starts capturing debug output from the specified process.
    /// Captures OutputDebugString messages and exception events.
    /// </summary>
    /// <param name="processId">The PID of the target process.</param>
    /// <param name="output">TextWriter to write captured debug output to (typically Console.Error).</param>
    /// <param name="captureDebug">Whether to output app-level OutputDebugString messages (filtered).</param>
    /// <param name="captureDebugAll">Whether to output all OutputDebugString messages including system runtime noise.</param>
    /// <param name="captureExceptions">Whether to output exception events.</param>
    /// <param name="cancellationToken">Cancellation token to stop capturing.</param>
    /// <returns>A task that completes when the target process exits or capturing is stopped.</returns>
    Task RunDebugEventLoopAsync(uint processId, TextWriter output, bool captureDebug = true, bool captureDebugAll = false, bool captureExceptions = true, CancellationToken cancellationToken = default);
}

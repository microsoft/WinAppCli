// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Services;

internal interface IDebugOutputService
{
    /// <summary>
    /// Starts capturing debug output from the specified process.
    /// Captures OutputDebugString messages and exception events.
    /// The calling thread must be the same thread that will call WaitForDebugEventEx.
    /// </summary>
    /// <param name="processId">The PID of the target process.</param>
    /// <param name="processHandle">A handle to the target process (from CREATE_PROCESS_DEBUG_EVENT).</param>
    /// <param name="output">TextWriter to write captured debug output to (typically Console.Error).</param>
    /// <param name="cancellationToken">Cancellation token to stop capturing.</param>
    /// <returns>A task that completes when the target process exits or capturing is stopped.</returns>
    Task RunDebugEventLoopAsync(uint processId, TextWriter output, CancellationToken cancellationToken = default);
}

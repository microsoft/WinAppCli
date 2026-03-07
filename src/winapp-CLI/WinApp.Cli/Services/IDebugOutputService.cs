// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Attaches to a running process as a debugger to capture <c>OutputDebugString</c>
/// messages and first-chance exceptions, similar to Visual Studio's Output window.
/// Only one debugger can attach to a process at a time — using this service
/// prevents other debuggers (Visual Studio, VS Code) from attaching.
/// </summary>
internal interface IDebugOutputService
{
    /// <summary>
    /// Attaches to the specified process using the Win32 Debug API and writes
    /// captured debug output and exception information to the console until
    /// the process exits or the <paramref name="cancellationToken"/> is signaled.
    /// When cancelled, the debugged process is terminated before returning.
    /// </summary>
    /// <param name="processId">The ID of the process to attach to.</param>
    /// <param name="cancellationToken">Token to stop the debug loop (e.g. Ctrl+C). The debugged process is terminated when this token is signaled.</param>
    /// <returns>The exit code of the debugged process, or <c>-1</c> if terminated early.</returns>
    Task<int> RunDebugLoopAsync(uint processId, CancellationToken cancellationToken);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;

namespace WinApp.Cli.Services;

/// <summary>
/// Formats and prints crash diagnostics after a process exits abnormally.
/// Combines crash hook exception data, Event Log queries, and exit code
/// interpretation into a single unified crash report.
/// </summary>
internal interface ICrashReportService
{
    /// <summary>
    /// Prints a human-readable exit summary. For crash codes (NTSTATUS/HRESULT with
    /// high bit set), includes the exception name. No-ops for exit code 0.
    /// </summary>
    void PrintExitCodeSummary(int exitCode);

    /// <summary>
    /// Prints managed exception details from the crash hook if available,
    /// otherwise falls back to the Windows Event Log. Call after the process
    /// has exited and the crash hook pipe has drained.
    /// </summary>
    /// <param name="crashHookService">The crash hook service with captured exceptions.</param>
    /// <param name="processId">PID of the crashed process (for Event Log queries).</param>
    /// <param name="crashTime">UTC time when the crash started (for Event Log time filter).</param>
    Task PrintCrashReportAsync(ICrashHookService crashHookService, uint processId, DateTime crashTimeUtc);
}

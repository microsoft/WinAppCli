// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Writes a minidump for a crashed process and optionally analyzes
/// it using CDB (Console Debugger) to produce a human-readable crash report.
/// </summary>
internal interface ICrashDumpService
{
    /// <summary>
    /// Writes a minidump of the specified process and returns the dump file path.
    /// Must be called while the process is still alive (e.g., after a second-chance
    /// exception before continuing with <c>DBG_EXCEPTION_NOT_HANDLED</c>).
    /// </summary>
    /// <param name="processId">The ID of the process to dump.</param>
    /// <param name="threadId">The ID of the faulting thread.</param>
    /// <param name="savedContext">Thread context bytes captured at first-chance time, or null.</param>
    /// <param name="savedThreadId">Thread ID from the first-chance exception.</param>
    /// <param name="savedExceptionCode">Exception code from the first-chance exception.</param>
    /// <param name="savedExceptionAddress">Exception address from the first-chance exception.</param>
    /// <returns>The full path to the dump file, or <c>null</c> if the dump failed.</returns>
    string? WriteMiniDump(uint processId, uint threadId,
        byte[]? savedContext, uint savedThreadId,
        int savedExceptionCode, nuint savedExceptionAddress);

    /// <summary>
    /// Analyzes a minidump using CDB and prints a crash summary to the console.
    /// Full CDB output is appended to the log file for detailed investigation.
    /// If CDB is not installed, prints the dump file path and installation instructions.
    /// </summary>
    /// <param name="dumpPath">Path to the minidump file.</param>
    /// <param name="logPath">Path to the debug log file where full analysis is appended.</param>
    Task AnalyzeDumpAsync(string dumpPath, string logPath);
}

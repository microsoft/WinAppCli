// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Writes a minidump for a crashed process and analyzes it using ClrMD
/// for managed exceptions and DbgEng for native stack traces.
/// </summary>
internal interface ICrashDumpService
{
    /// <summary>
    /// Writes a minidump of the specified process and returns the dump file path.
    /// Must be called while the process is still alive (e.g., after a second-chance
    /// exception before continuing with <c>DBG_EXCEPTION_NOT_HANDLED</c>).
    /// </summary>
    /// <param name="processId">The ID of the process to dump.</param>
    /// <param name="savedContext">Thread context bytes captured at first-chance time, or null.</param>
    /// <param name="savedThreadId">Thread ID from the first-chance exception.</param>
    /// <param name="savedExceptionCode">Exception code from the first-chance exception.</param>
    /// <param name="savedExceptionAddress">Exception address from the first-chance exception.</param>
    /// <returns>The full path to the dump file, or <c>null</c> if the dump failed.</returns>
    string? WriteMiniDump(uint processId,
        byte[]? savedContext, uint savedThreadId,
        int savedExceptionCode, nuint savedExceptionAddress);

    /// <summary>
    /// Analyzes a minidump and prints a crash summary to the console.
    /// Uses ClrMD for managed exceptions; falls back to DbgEng for native stack traces.
    /// Full analysis output is appended to the log file for detailed investigation.
    /// </summary>
    /// <param name="dumpPath">Path to the minidump file.</param>
    /// <param name="logPath">Path to the debug log file where full analysis is appended.</param>
    /// <param name="useSymbols">When true, downloads symbols from Microsoft Symbol Server for richer native analysis.</param>
    /// <param name="symbolSearchPaths">Additional directories to search for PDB files (e.g., the build output folder). Used to resolve source file and line numbers in managed stack traces.</param>
    Task AnalyzeDumpAsync(string dumpPath, string logPath, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null);
}

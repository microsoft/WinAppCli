// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma warning disable CA1416

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Threading;

namespace WinApp.Cli.Services;

/// <summary>
/// Writes minidumps for crashed processes and analyzes them using CDB
/// (Console Debugger) to produce human-readable crash reports with stack traces.
/// </summary>
internal sealed class CrashDumpService(IAnsiConsole console, ILogger<CrashDumpService> logger) : ICrashDumpService
{
    private static readonly string DumpDirectory = Path.Combine(Path.GetTempPath(), "winapp-dumps");

    /// <inheritdoc/>
    public unsafe string? WriteMiniDump(uint processId, uint threadId,
        byte[]? savedContext, uint savedThreadId,
        int savedExceptionCode, nuint savedExceptionAddress)
    {
        try
        {
            Directory.CreateDirectory(DumpDirectory);
            var dumpPath = Path.Combine(DumpDirectory, $"crash-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");

            using var processHandle = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_ALL_ACCESS, false, processId);

            if (processHandle.IsInvalid)
            {
                logger.LogError("Failed to open process {PID} for dump capture.", processId);
                return null;
            }

            using var fileHandle = File.Create(dumpPath);

            var dumpType =
                MINIDUMP_TYPE.MiniDumpWithFullMemory |
                MINIDUMP_TYPE.MiniDumpWithHandleData |
                MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
                MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                MINIDUMP_TYPE.MiniDumpWithThreadInfo;

            BOOL success;

            if (savedContext != null)
            {
                logger.LogDebug("Writing dump with saved first-chance context ({Bytes} bytes, thread {ThreadId}, code 0x{Code:X8}).",
                    savedContext.Length, savedThreadId, savedExceptionCode);

                // Use the first-chance context — it points to the user code that
                // originally caused the exception, before XAML's error handling
                // replaced the stack with FailFastWithStowedExceptions.
                fixed (byte* pContext = savedContext)
                {
                    var exRecord = new EXCEPTION_RECORD
                    {
                        ExceptionCode = new NTSTATUS(savedExceptionCode),
                        ExceptionAddress = (void*)savedExceptionAddress,
                        ExceptionRecord = null,
                    };

                    var exPtrs = new EXCEPTION_POINTERS
                    {
                        ExceptionRecord = &exRecord,
                        ContextRecord = (CONTEXT*)pContext,
                    };

                    var exInfo = new MINIDUMP_EXCEPTION_INFORMATION
                    {
                        ThreadId = savedThreadId,
                        ExceptionPointers = &exPtrs,
                        ClientPointers = false,
                    };

                    success = PInvoke.MiniDumpWriteDump(
                        processHandle,
                        processId,
                        fileHandle.SafeFileHandle,
                        dumpType,
                        exInfo,
                        UserStreamParam: null,
                        CallbackParam: null);
                }
            }
            else
            {
                success = PInvoke.MiniDumpWriteDump(
                    processHandle,
                    processId,
                    fileHandle.SafeFileHandle,
                    dumpType,
                    ExceptionParam: null,
                    UserStreamParam: null,
                    CallbackParam: null);
            }

            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                logger.LogError("MiniDumpWriteDump failed with error code {Error}.", error);
                return null;
            }

            logger.LogDebug("Crash dump written to {DumpPath} ({Size} bytes).", dumpPath, fileHandle.Length);
            return dumpPath;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write crash dump for process {PID}.", processId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task AnalyzeDumpAsync(string dumpPath, string logPath)
    {
        var cdbPath = FindCdb();
        if (cdbPath == null)
        {
            console.MarkupLine($"\n[red]Crash dump:[/] {dumpPath.EscapeMarkup()}");
            console.MarkupLine("[dim]Install WinDbg for automatic crash analysis:[/] [blue]winget install Microsoft.WinDbg[/]");
            return;
        }

        console.MarkupLine("[dim]Analyzing crash dump (first run may take a few minutes to download symbols)...[/]");

        try
        {
            var (summary, fullOutput) = await RunCdbAnalysisAsync(cdbPath, dumpPath);

            // Append full CDB output to the log file
            if (!string.IsNullOrWhiteSpace(fullOutput))
            {
                await File.AppendAllTextAsync(logPath,
                    $"\n\n=== CDB Analysis ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\n{fullOutput}\n");
            }

            console.WriteLine();
            console.MarkupLine("[red]========== CRASH DETECTED ==========[/]");
            console.MarkupLine($"[red]Crash dump:[/] {dumpPath.EscapeMarkup()}");

            if (!string.IsNullOrWhiteSpace(summary))
            {
                console.WriteLine();
                console.MarkupLine("[red][[CRASH ANALYSIS]][/]");
                console.WriteLine(summary);
                console.MarkupLine("[red]=====================================[/]");
            }

            console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CDB analysis failed.");
            console.MarkupLine($"\n[red]Crash dump:[/] {dumpPath.EscapeMarkup()}");
            console.MarkupLine($"[dim]CDB analysis failed. Open in WinDbg:[/] [blue]windbg -z \"{dumpPath}\"[/]");
            console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
        }
    }

    private static string? FindCdb()
    {
        // 1. Check traditional Windows SDK paths
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var sdkPaths = new[]
        {
            $@"C:\Program Files (x86)\Windows Kits\10\Debuggers\{arch}\cdb.exe",
            $@"C:\Program Files\Windows Kits\10\Debuggers\{arch}\cdb.exe",
        };

        foreach (var path in sdkPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // 2. Check WinDbg Store app via PackageManager (avoids WindowsApps ACL issues)
        try
        {
            var packageManager = new Windows.Management.Deployment.PackageManager();
            var packages = packageManager.FindPackagesForUser(string.Empty, "Microsoft.WinDbg_8wekyb3d8bbwe");
            foreach (var pkg in packages)
            {
                var cdbArch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
                var cdbPath = Path.Combine(pkg.InstalledPath, cdbArch, "cdb.exe");
                if (File.Exists(cdbPath))
                {
                    return cdbPath;
                }
            }
        }
        catch
        {
            // PackageManager API may not be available in all contexts
        }

        // 3. Check PATH
        try
        {
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
            foreach (var dir in pathDirs)
            {
                var candidate = Path.Combine(dir, "cdb.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Ignore PATH parsing errors
        }

        return null;
    }

    private static async Task<(string Summary, string FullOutput)> RunCdbAnalysisAsync(string cdbPath, string dumpPath)
    {
        // Configure CDB to:
        // - Use Microsoft Symbol Server for WinUI/system symbols
        // - .ecxr: switch to exception context record
        // - kp: display stack trace with parameters
        // - !analyze -v: verbose crash analysis
        // - q: quit
        var symbolPath = $"srv*{Path.Combine(Path.GetTempPath(), "symbols")}*https://msdl.microsoft.com/download/symbols";
        var commands = "!sym quiet; .ecxr; kp 50; !analyze -v; q";

        var psi = new ProcessStartInfo
        {
            FileName = cdbPath,
            ArgumentList = { "-y", symbolPath, "-z", dumpPath, "-c", commands },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return (string.Empty, string.Empty);
        }

        // CDB may take time to download symbols on first run
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return (ExtractRelevantOutput(output), output);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return ("[CDB analysis timed out after 300 seconds]", string.Empty);
        }
    }

    /// <summary>
    /// Extracts a concise crash summary from CDB output for terminal display.
    /// Full output is written to the log file separately.
    /// </summary>
    private static string ExtractRelevantOutput(string fullOutput)
    {
        var lines = fullOutput.Split('\n');
        var result = new System.Text.StringBuilder();
        var stackFrames = new List<string>();
        var analyzeStackFrames = new List<string>();
        var managedException = new System.Text.StringBuilder();
        var inStack = false;
        var inAnalyzeStack = false;
        var inManagedEx = false;

        // Key fields from !analyze -v worth showing in terminal
        ReadOnlySpan<string> keyFields =
        [
            "SYMBOL_NAME:",
            "MODULE_NAME:",
            "IMAGE_NAME:",
            "FAULTING_SOURCE_FILE:",
            "FAULTING_SOURCE_LINE_NUMBER:",
            "FAILURE_BUCKET_ID:",
        ];

        // Framework modules to collapse in the stack display
        ReadOnlySpan<string> frameworkPrefixes =
        [
            "ntdll!",
            "KERNELBASE!",
            "kernel32!",
            "combase!",
            "twinapi_appcore!",
            "hostfxr!",
            "hostpolicy!",
            "ucrtbase!",
        ];

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Capture !pe output (managed exception type + message + stack)
            if (line.StartsWith("Exception object:") || line.StartsWith("Exception type:"))
            {
                inManagedEx = true;
            }

            if (inManagedEx)
            {
                if (line.StartsWith("Exception type:") || line.StartsWith("Message:") ||
                    line.StartsWith("InnerException:") || line.StartsWith("StackTrace (generated):") ||
                    line.StartsWith("HResult:"))
                {
                    managedException.AppendLine(line);
                }
                // Capture managed stack frames (indented with spaces)
                else if (managedException.Length > 0 && line.StartsWith("    "))
                {
                    managedException.AppendLine(line);
                }
                else if (string.IsNullOrWhiteSpace(line) && managedException.Length > 0)
                {
                    inManagedEx = false;
                }
            }

            // Capture STACK_TEXT from !analyze -v
            if (line.StartsWith("STACK_TEXT:"))
            {
                inAnalyzeStack = true;
                continue;
            }

            if (inAnalyzeStack)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("SYMBOL_NAME") || line.StartsWith("MODULE_NAME"))
                {
                    inAnalyzeStack = false;
                    // Fall through to check keyFields
                }
                else
                {
                    var callSite = ExtractCallSite(line);
                    if (callSite != null)
                    {
                        analyzeStackFrames.Add(callSite);
                    }
                    continue;
                }
            }

            // Capture kp stack frames
            if (line.StartsWith("Child-SP") || line.StartsWith(" # Child-SP") || line.StartsWith(" #  Child-SP"))
            {
                inStack = true;
                continue;
            }

            if (inStack)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    inStack = false;
                }
                else
                {
                    var callSite = ExtractCallSite(line);
                    if (callSite != null)
                    {
                        stackFrames.Add(callSite);
                    }
                }
                continue;
            }

            // Capture key !analyze -v fields
            foreach (var field in keyFields)
            {
                if (line.StartsWith(field))
                {
                    result.AppendLine(line);
                    break;
                }
            }

            if (line.StartsWith("quit:"))
            {
                break;
            }
        }

        // Show managed exception info first (most useful for .NET apps)
        if (managedException.Length > 0)
        {
            result.AppendLine();
            result.AppendLine("Managed Exception:");
            result.Append(managedException);
        }

        // Prefer STACK_TEXT from !analyze -v (contains the faulting stack),
        // fall back to kp output
        var frames = analyzeStackFrames.Count > 0 ? analyzeStackFrames : stackFrames;
        if (frames.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("Stack:");

            var displayed = 0;
            var lastWasEllipsis = false;

            for (var i = 0; i < frames.Count && displayed < 15; i++)
            {
                var frame = frames[i];

                var isFramework = false;
                foreach (var prefix in frameworkPrefixes)
                {
                    if (frame.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        isFramework = true;
                        break;
                    }
                }

                if (isFramework)
                {
                    if (!lastWasEllipsis)
                    {
                        result.AppendLine("    ...");
                        lastWasEllipsis = true;
                        displayed++;
                    }
                }
                else
                {
                    var displayFrame = TruncateFrame(frame, 80);
                    result.AppendLine($"  {displayFrame}");
                    lastWasEllipsis = false;
                    displayed++;
                }
            }

            if (displayed < frames.Count)
            {
                result.AppendLine($"  ... ({frames.Count - displayed} more frames in log)");
            }
        }

        return result.ToString().Trim();
    }

    private static string? ExtractCallSite(string line)
    {
        var bangIdx = line.IndexOf('!');
        if (bangIdx < 0) return null;

        // Find start of "Module!Function"
        var start = bangIdx;
        while (start > 0 && line[start - 1] != ' ') start--;

        var rest = line[start..];

        // Strip parameters
        var parenIdx = rest.IndexOf('(');
        if (parenIdx > 0) rest = rest[..parenIdx];

        return rest;
    }

    private static string TruncateFrame(string frame, int maxLen)
    {
        if (frame.Length <= maxLen) return frame;

        var cutAt = frame.LastIndexOf('<', Math.Min(maxLen, frame.Length - 1));
        if (cutAt > 20) return frame[..cutAt] + "<...>";

        return frame[..maxLen] + "...";
    }
}

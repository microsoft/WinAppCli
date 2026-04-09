// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma warning disable CA1416

using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Threading;

namespace WinApp.Cli.Services;

/// <summary>
/// Writes minidumps for crashed processes and analyzes them using ClrMD
/// to produce human-readable crash reports with managed exception details and stack traces.
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
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ,
                false, processId);

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
                // CONTEXT must be 16-byte aligned on x64/ARM64. The saved byte[]
                // from a managed array doesn't guarantee this, so copy into an
                // aligned native buffer.
                var pContext = (CONTEXT*)NativeMemory.AlignedAlloc((nuint)savedContext.Length, 16);
                try
                {
                    fixed (byte* pSaved = savedContext)
                    {
                        Buffer.MemoryCopy(pSaved, pContext, savedContext.Length, savedContext.Length);
                    }

                    var exRecord = new EXCEPTION_RECORD
                    {
                        ExceptionCode = new NTSTATUS(savedExceptionCode),
                        ExceptionAddress = (void*)savedExceptionAddress,
                        ExceptionRecord = null,
                    };

                    var exPtrs = new EXCEPTION_POINTERS
                    {
                        ExceptionRecord = &exRecord,
                        ContextRecord = pContext,
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
                finally
                {
                    NativeMemory.AlignedFree(pContext);
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
        console.MarkupLine("[dim]Analyzing crash dump...[/]");

        try
        {
            var (summary, details) = await Task.Run(() => AnalyzeWithClrMD(dumpPath));

            if (!string.IsNullOrWhiteSpace(details))
            {
                await File.AppendAllTextAsync(logPath,
                    $"\n\n=== Crash Analysis ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\n{details}\n");
            }

            console.WriteLine();
            console.MarkupLine("[red]========== CRASH DETECTED ==========[/]");

            if (!string.IsNullOrWhiteSpace(summary))
            {
                console.WriteLine();
                console.MarkupLine("[red][[CRASH ANALYSIS]][/]");
                console.WriteLine(summary);
                console.MarkupLine("[red]=====================================[/]");
            }
            else
            {
                console.MarkupLine("[dim]No managed exception found. For native crash analysis:[/]");
                console.MarkupLine($"[blue]windbg -z \"{dumpPath.EscapeMarkup()}\"[/]");
            }

            console.MarkupLine($"[dim]Crash dump:[/] {dumpPath.EscapeMarkup()}");
            console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Crash analysis failed.");
            console.MarkupLine($"\n[red]Crash dump:[/] {dumpPath.EscapeMarkup()}");
            console.MarkupLine($"[dim]Analysis failed. Open in WinDbg:[/] [blue]windbg -z \"{dumpPath.EscapeMarkup()}\"[/]");
            console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
        }
    }

    private static (string Summary, string Details) AnalyzeWithClrMD(string dumpPath)
    {
        using var dt = DataTarget.LoadDump(dumpPath);

        if (dt.ClrVersions.Length == 0)
        {
            return (string.Empty, "No CLR runtime found in dump (native-only crash).");
        }

        using var runtime = dt.ClrVersions[0].CreateRuntime();

        var summary = new StringBuilder();
        var details = new StringBuilder();

        details.AppendLine($"CLR Version: {dt.ClrVersions[0].Version}");
        details.AppendLine($"Target Architecture: {dt.DataReader.Architecture}");

        // 1. Check threads for CurrentException
        ClrException? exception = null;
        foreach (var thread in runtime.Threads)
        {
            if (thread.CurrentException != null)
            {
                exception = thread.CurrentException;
                break;
            }
        }

        // 2. WinUI's FailFast clears the thread exception — scan the heap as fallback.
        //    Skip pre-allocated singletons (OOM, SOE, EEE) that have no stack trace.
        //    Take the last match — Gen0 (most recently allocated) objects appear later
        //    in the enumeration, so the crash-causing exception is more likely at the end.
        //    Note: full-memory dumps can be hundreds of MB; heap enumeration is O(heap size)
        //    but typically completes in a few seconds even for large dumps.
        if (exception == null)
        {
            foreach (var seg in runtime.Heap.Segments)
            {
                foreach (var obj in seg.EnumerateObjects())
                {
                    if (obj.Type is not { IsException: true })
                    {
                        continue;
                    }

                    var candidate = obj.AsException();
                    if (candidate?.StackTrace.Length > 0)
                    {
                        exception = candidate;
                    }
                }
            }
        }

        if (exception != null)
        {
            FormatException(exception, summary, details);
        }

        // 3. No exception found — check for Stack Overflow by finding a thread with
        //    a very deep stack (hundreds of repeated frames from infinite recursion).
        //    Materialize frames once per thread to avoid double enumeration.
        if (exception == null)
        {
            List<ClrStackFrame>? deepestFrames = null;
            ClrThread? deepest = null;

            foreach (var thread in runtime.Threads)
            {
                var frames = thread.EnumerateStackTrace().Where(f => f.Method != null).ToList();
                if (frames.Count > (deepestFrames?.Count ?? 0))
                {
                    deepestFrames = frames;
                    deepest = thread;
                }
            }

            if (deepest != null && deepestFrames != null && deepestFrames.Count > 100)
            {
                summary.AppendLine("Exception: Stack Overflow (deep recursion detected)");
                summary.AppendLine($"Thread: {deepest.OSThreadId} ({deepestFrames.Count} managed frames)");
                summary.AppendLine();
                summary.AppendLine("Stack:");
                string? lastFrame = null;
                var repeatCount = 0;
                var displayed = 0;

                foreach (var frame in deepestFrames)
                {
                    if (displayed >= 15)
                    {
                        break;
                    }

                    var name = $"{frame.Method!.Type?.Name}.{frame.Method!.Name}";
                    if (name == lastFrame)
                    {
                        repeatCount++;
                        continue;
                    }

                    if (repeatCount > 0)
                    {
                        summary.AppendLine($"  ... (repeated {repeatCount} more times)");
                        displayed++;
                    }

                    if (displayed >= 15)
                    {
                        break;
                    }

                    summary.AppendLine($"  {name}");
                    displayed++;
                    repeatCount = 0;
                    lastFrame = name;
                }

                if (repeatCount > 0 && displayed < 15)
                {
                    summary.AppendLine($"  ... (repeated {repeatCount} more times)");
                }
            }
        }

        // All threads in detailed log
        details.AppendLine("\n=== All Threads ===");
        foreach (var thread in runtime.Threads)
        {
            var frames = thread.EnumerateStackTrace().ToList();
            if (frames.Count == 0)
            {
                continue;
            }

            details.AppendLine($"\nThread {thread.OSThreadId} (Managed ID: {thread.ManagedThreadId}):");
            if (thread.CurrentException != null)
            {
                details.AppendLine($"  ** Exception: {thread.CurrentException.Type?.Name} **");
            }

            foreach (var frame in frames)
            {
                if (frame.Method != null)
                {
                    details.AppendLine($"  {frame.Method.Type?.Module?.Name}!{frame.Method.Type?.Name}.{frame.Method.Name}");
                }
            }
        }

        return (summary.ToString().Trim(), details.ToString().Trim());
    }

    private static void FormatException(ClrException ex, StringBuilder summary, StringBuilder details)
    {
        // Console summary
        summary.AppendLine($"Exception: {ex.Type?.Name}");
        if (!string.IsNullOrEmpty(ex.Message))
        {
            summary.AppendLine($"Message: {ex.Message}");
        }

        var inner = ex.Inner;
        while (inner != null)
        {
            summary.AppendLine($"Inner: {inner.Type?.Name}: {inner.Message}");
            inner = inner.Inner;
        }

        if (ex.StackTrace.Length > 0)
        {
            summary.AppendLine();
            summary.AppendLine("Stack:");
            var limit = Math.Min(ex.StackTrace.Length, 15);
            for (var i = 0; i < limit; i++)
            {
                var method = ex.StackTrace[i].Method;
                if (method != null)
                {
                    summary.AppendLine($"  {method.Type?.Name}.{method.Name}");
                }
            }

            if (ex.StackTrace.Length > 15)
            {
                summary.AppendLine($"  ... ({ex.StackTrace.Length - 15} more frames in log)");
            }
        }

        // Detailed log
        details.AppendLine($"\nException Type: {ex.Type?.Name}");
        details.AppendLine($"Message: {ex.Message}");
        details.AppendLine($"HResult: 0x{ex.HResult:X8}");

        inner = ex.Inner;
        var depth = 1;
        while (inner != null)
        {
            details.AppendLine($"\nInner Exception [{depth}]: {inner.Type?.Name}");
            details.AppendLine($"  Message: {inner.Message}");
            details.AppendLine($"  HResult: 0x{inner.HResult:X8}");
            inner = inner.Inner;
            depth++;
        }

        details.AppendLine("\nException Stack Trace:");
        foreach (var frame in ex.StackTrace)
        {
            var method = frame.Method;
            if (method != null)
            {
                details.AppendLine($"  {method.Type?.Module?.Name}!{method.Type?.Name}.{method.Name}");
            }
        }
    }
}

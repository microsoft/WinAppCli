// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// The CLI requires Windows 10+; suppress platform compat warnings for Debug APIs.
#pragma warning disable CA1416

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
/// Attaches to a running process via the Win32 Debug API and streams
/// <c>OutputDebugString</c> messages and first-chance exceptions to the console.
/// Only one debugger can attach to a process at a time — using this service
/// prevents other debuggers (Visual Studio, VS Code) from attaching.
/// The debugged process is terminated when the debug session ends (e.g., Ctrl+C)
/// or if the winapp process exits unexpectedly.
/// </summary>
internal sealed class DebugOutputService(IAnsiConsole console, ICrashReportService crashReportService, ILogger<DebugOutputService> logger) : IDebugOutputService
{
    // Well-known NTSTATUS / exception codes
    private const uint STATUS_BREAKPOINT = 0x80000003;
    private const uint STATUS_SINGLE_STEP = 0x80000004;
    private const uint STATUS_WX86_BREAKPOINT = 0x4000001F;
    private const uint THREAD_NAME_EXCEPTION = 0x406D1388;

    /// <summary>
    /// Loaded module info: base address and file name.
    /// Sorted by base address for efficient lookup.
    /// </summary>
    private readonly SortedList<nuint, string> _modules = new();

    /// <summary>
    /// Directory to write crash dumps to, or null to disable.
    /// </summary>
    private string? _crashDumpDirectory;

    /// <inheritdoc/>
    public Task<int> RunDebugLoopAsync(uint processId, string? crashDumpDirectory, CancellationToken cancellationToken)
    {
        _crashDumpDirectory = crashDumpDirectory;

        // DebugActiveProcess + WaitForDebugEventEx must be called from the same thread,
        // so spin up a dedicated thread via Task.Run.
        return Task.Run(() => RunDebugLoop(processId, cancellationToken), cancellationToken);
    }

    private int RunDebugLoop(uint processId, CancellationToken cancellationToken)
    {
        // If winapp crashes without cleanup, the OS terminates the debuggee.
        PInvoke.DebugSetProcessKillOnExit(true);

        if (!PInvoke.DebugActiveProcess(processId))
        {
            logger.LogError(
                "Failed to attach debugger to process {PID}. The process may have exited before the debugger could attach. " +
                "For short-lived apps, consider using --with-alias instead.",
                processId);
            return -1;
        }

        logger.LogDebug("Attached debugger to process {PID}.", processId);

        try
        {
            return DebugEventLoop(processId, cancellationToken);
        }
        finally
        {
            PInvoke.DebugActiveProcessStop(processId);
            logger.LogDebug("Detached debugger from process {PID}.", processId);
        }
    }

    private unsafe int DebugEventLoop(uint processId, CancellationToken cancellationToken)
    {
        int exitCode = -1;
        bool initialBreakpointSeen = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Poll with a short timeout so we can check the cancellation token.
            if (!PInvoke.WaitForDebugEventEx(out var debugEvent, 100))
            {
                continue;
            }

            var continueStatus = NTSTATUS.DBG_CONTINUE;

            switch (debugEvent.dwDebugEventCode)
            {
                case DEBUG_EVENT_CODE.OUTPUT_DEBUG_STRING_EVENT:
                    HandleOutputDebugString(in debugEvent);
                    break;

                case DEBUG_EVENT_CODE.EXCEPTION_DEBUG_EVENT:
                    HandleException(in debugEvent, ref initialBreakpointSeen, ref continueStatus);
                    break;

                case DEBUG_EVENT_CODE.EXIT_PROCESS_DEBUG_EVENT:
                    exitCode = unchecked((int)debugEvent.u.ExitProcess.dwExitCode);
                    PrintExitSummary(exitCode);
                    PInvoke.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
                    return exitCode;

                case DEBUG_EVENT_CODE.CREATE_PROCESS_DEBUG_EVENT:
                    HandleCreateProcess(in debugEvent);
                    break;

                case DEBUG_EVENT_CODE.LOAD_DLL_DEBUG_EVENT:
                    HandleLoadDll(in debugEvent);
                    break;

                case DEBUG_EVENT_CODE.UNLOAD_DLL_DEBUG_EVENT:
                    var unloadBase = (nuint)debugEvent.u.UnloadDll.lpBaseOfDll;
                    _modules.Remove(unloadBase);
                    break;
            }

            PInvoke.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
        }

        return exitCode;
    }

    private unsafe void HandleCreateProcess(in DEBUG_EVENT debugEvent)
    {
        var info = debugEvent.u.CreateProcessInfo;
        var baseAddress = (nuint)info.lpBaseOfImage;
        var name = GetModuleNameFromHandle(info.hFile);
        if (name is not null)
        {
            _modules[baseAddress] = name;
        }
        CloseHandleSafe(info.hFile);
    }

    private unsafe void HandleLoadDll(in DEBUG_EVENT debugEvent)
    {
        var info = debugEvent.u.LoadDll;
        var baseAddress = (nuint)info.lpBaseOfDll;
        var name = GetModuleNameFromHandle(info.hFile);
        if (name is not null)
        {
            _modules[baseAddress] = name;
        }
        CloseHandleSafe(info.hFile);
    }

    /// <summary>
    /// Gets the file name of a module from its file handle using GetFinalPathNameByHandle.
    /// </summary>
    private static unsafe string? GetModuleNameFromHandle(HANDLE hFile)
    {
        if (hFile.IsNull || hFile.Value == (void*)-1)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[512];
        fixed (char* pBuffer = buffer)
        {
            var length = PInvoke.GetFinalPathNameByHandle(hFile, pBuffer, (uint)buffer.Length, 0);
            if (length == 0 || length > buffer.Length)
            {
                return null;
            }

            var fullPath = new string(pBuffer, 0, (int)length);
            // Strip the \\?\ prefix that GetFinalPathNameByHandle adds
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                fullPath = fullPath[4..];
            }
            return Path.GetFileName(fullPath);
        }
    }

    /// <summary>
    /// Resolves a code address to a module name + offset string (e.g., "Microsoft.WinUI.dll+0x1234").
    /// Falls back to the raw hex address if no module is found.
    /// </summary>
    private string ResolveAddress(nuint address)
    {
        // Binary search for the module with the highest base address ≤ the target address
        var keys = _modules.Keys;
        int lo = 0, hi = keys.Count - 1;
        int bestIndex = -1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keys[mid] <= address)
            {
                bestIndex = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (bestIndex >= 0)
        {
            var baseAddr = keys[bestIndex];
            var name = _modules.Values[bestIndex];
            var offset = address - baseAddr;
            return $"{name}+0x{offset:X}";
        }

        return $"0x{address:X}";
    }

    private unsafe void HandleOutputDebugString(in DEBUG_EVENT debugEvent)
    {
        var info = debugEvent.u.DebugString;
        int length = Math.Min((int)info.nDebugStringLength, 65534);
        if (length == 0)
        {
            return;
        }

        using var processHandle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, debugEvent.dwProcessId);

        if (processHandle.IsInvalid)
        {
            return;
        }

        Span<byte> buffer = length <= 4096 ? stackalloc byte[length] : new byte[length];

        if (!PInvoke.ReadProcessMemory(processHandle, info.lpDebugStringData, buffer, out var bytesRead) || bytesRead == 0)
        {
            return;
        }

        var usable = buffer[..(int)bytesRead];
        string message = info.fUnicode != 0
            ? Encoding.Unicode.GetString(usable)
            : Encoding.Default.GetString(usable);

        message = message.TrimEnd('\0');

        if (!string.IsNullOrWhiteSpace(message))
        {
            // Trim trailing newline so Spectre doesn't double-space the output.
            message = message.TrimEnd('\r', '\n');

            // Filter out OS/framework internal debug messages — these are noise from
            // WinUI, COM, DirectX, and other system DLLs during normal operation.
            // App-specific debug messages (System.Diagnostics.Debug.WriteLine, etc.) pass through.
            if (IsFrameworkNoise(message))
            {
                return;
            }

            console.MarkupLine($"[dim][[Debug]][/] {message.EscapeMarkup()}");
        }
    }

    /// <summary>
    /// Returns true if the debug message is internal OS/framework noise
    /// rather than an app-specific debug message worth showing.
    /// </summary>
    private static bool IsFrameworkNoise(string message)
    {
        // Windows OS source paths (onecore, onecoreuap, minkernel, etc.)
        if (message.StartsWith("onecore\\", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("onecoreuap\\", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("minkernel\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // WinRT/COM internal trace markers
        if (message.Contains("ReturnHr(", StringComparison.Ordinal) ||
            message.Contains("LogHr(", StringComparison.Ordinal) ||
            message.Contains("ReturnNt(", StringComparison.Ordinal))
        {
            return true;
        }

        // Windows SDK build paths (C:\__w\1\s\ is the Azure DevOps build agent path)
        if (message.StartsWith("C:\\__w\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Framework DLL WIL/HRESULT trace format: "DllName.dll!0x..." or "DllName.dll!FuncName"
        // These come from WinUI, DirectX, WinRT runtime, and other system DLLs.
        // Match patterns like: "Microsoft.UI.Xaml.dll!0x000...", "twinapi.appcore.dll!..."
        if (IsFrameworkDllTrace(message))
        {
            return true;
        }

        // Common framework HRESULT noise (E_INVALIDARG, E_FAIL, etc.)
        if (message.StartsWith("E_INVALIDARG", StringComparison.Ordinal) ||
            message.StartsWith("E_FAIL", StringComparison.Ordinal) ||
            message.StartsWith("HRESULT:", StringComparison.Ordinal) ||
            message.StartsWith("hr = ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the message looks like a framework DLL debug trace
    /// (e.g., "Microsoft.UI.Xaml.dll!0x..." or "twinapi.appcore.dll!SomeFunc").
    /// </summary>
    private static bool IsFrameworkDllTrace(string message)
    {
        var bangIndex = message.IndexOf('!');
        if (bangIndex < 5) // Need at least "x.dll" before the '!'
        {
            return false;
        }

        // Check if the part before '!' ends with ".dll" (case-insensitive)
        var beforeBang = message.AsSpan(0, bangIndex);
        if (!beforeBang.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check if it's a known framework/system DLL prefix
        if (beforeBang.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Microsoft.Web.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Microsoft.WinUI.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("twinapi", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("dxgi", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("d3d", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("d2d", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("combase", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("oleaut32", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("ntdll", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("kernelbase", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("kernel32", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("WinAppRuntime", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("MRM", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private unsafe void HandleException(
        in DEBUG_EVENT debugEvent,
        ref bool initialBreakpointSeen,
        ref NTSTATUS continueStatus)
    {
        var exInfo = debugEvent.u.Exception;
        uint code = unchecked((uint)exInfo.ExceptionRecord.ExceptionCode.Value);
        bool firstChance = exInfo.dwFirstChance != 0;

        // Suppress the initial breakpoint that the OS sends when we attach.
        if (!initialBreakpointSeen && (code is STATUS_BREAKPOINT or STATUS_WX86_BREAKPOINT))
        {
            initialBreakpointSeen = true;
            continueStatus = NTSTATUS.DBG_CONTINUE;
            return;
        }

        // Suppress single-step and thread-name exceptions — they are noise.
        if (code is STATUS_SINGLE_STEP or THREAD_NAME_EXCEPTION)
        {
            continueStatus = NTSTATUS.DBG_CONTINUE;
            return;
        }

        var name = GetExceptionName(code);
        var address = (nuint)exInfo.ExceptionRecord.ExceptionAddress;
        var location = ResolveAddress(address);

        if (firstChance)
        {
            // Only show first-chance exceptions that are meaningful for crash diagnosis:
            // - CLR exceptions (0xE0434352): managed .NET exceptions
            // - Access Violations (0xC0000005): null pointer dereferences
            // - Stack Overflow (0xC00000FD): recursion too deep
            // Skip WinUI/COM internal exceptions (0x40080201, 0x04242420, etc.) that
            // are caught and handled during normal framework operation.
            const uint CLR_EXCEPTION = 0xE0434352;
            const uint ACCESS_VIOLATION = 0xC0000005;
            const uint STACK_OVERFLOW = 0xC00000FD;
            if (code is CLR_EXCEPTION or ACCESS_VIOLATION or STACK_OVERFLOW)
            {
                console.MarkupLine($"[yellow]First-chance exception:[/] {name} (0x{code:X8}) at {location.EscapeMarkup()}");
            }
        }
        else
        {
            // Second-chance exception = actual crash.
            console.WriteLine();
            console.MarkupLine("[red bold]╔══════════════════════════════════════╗[/]");
            console.MarkupLine("[red bold]║         APPLICATION CRASHED          ║[/]");
            console.MarkupLine("[red bold]╚══════════════════════════════════════╝[/]");
            console.MarkupLine($"[red]Exception:[/]  {name} (0x{code:X8})");

            // Initialize dbghelp symbols for crash location resolution
            using var symHandle = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
                PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ,
                false, debugEvent.dwProcessId);

            bool symInitialized = false;
            if (!symHandle.IsInvalid)
            {
                _ = PInvoke.SymSetOptions(0x2 | 0x4 | 0x10);
                symInitialized = PInvoke.SymInitialize(symHandle, (string?)null, true);
            }

            try
            {
                var richLocation = symInitialized
                    ? ResolveStackFrame(symHandle, (ulong)address)
                    : location;
                console.MarkupLine($"[red]Location:[/]   {richLocation.EscapeMarkup()}");

                const uint STATUS_STOWED_EXCEPTION = 0xC000027B;
                if (code == STATUS_STOWED_EXCEPTION)
                {
                    ReadStowedExceptions(debugEvent.dwProcessId, in exInfo.ExceptionRecord);
                }

                WriteCrashDump(debugEvent.dwProcessId, debugEvent.dwThreadId);
            }
            finally
            {
                if (symInitialized)
                {
                    PInvoke.SymCleanup(symHandle);
                }
            }

            console.WriteLine();
        }

        // Let the target's own exception handling run. For second-chance exceptions
        // (firstChance == false), this causes the OS to terminate the process — correct
        // behavior for a passive listener that doesn't handle exceptions itself.
        continueStatus = NTSTATUS.DBG_EXCEPTION_NOT_HANDLED;
    }

    /// <summary>
    /// Delegates exit summary printing to the crash report service.
    /// </summary>
    private void PrintExitSummary(int exitCode)
    {
        crashReportService.PrintExitCodeSummary(exitCode);
    }

    /// <summary>
    /// Writes a mini-dump of the crashed process. Called on second-chance exception
    /// while the process is still alive (frozen by the debug loop). Since winapp is
    /// the out-of-process debugger, MiniDumpWriteDump is safe to call here without
    /// risk of deadlocks.
    /// </summary>
    private void WriteCrashDump(uint processId, uint threadId)
    {
        if (_crashDumpDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_crashDumpDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dumpPath = Path.Combine(_crashDumpDirectory, $"crash-{processId}-{timestamp}.dmp");

            using var processHandle = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
                PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ,
                false, processId);

            if (processHandle.IsInvalid)
            {
                logger.LogDebug("Failed to open process {PID} for crash dump.", processId);
                return;
            }

            using var fileStream = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithThreadInfo
            // produces a useful dump (~5-20MB) with all thread stacks and loaded modules.
            const MINIDUMP_TYPE dumpType =
                MINIDUMP_TYPE.MiniDumpWithDataSegs |
                MINIDUMP_TYPE.MiniDumpWithHandleData |
                MINIDUMP_TYPE.MiniDumpWithThreadInfo |
                MINIDUMP_TYPE.MiniDumpWithUnloadedModules;

            var result = PInvoke.MiniDumpWriteDump(
                processHandle,
                processId,
                fileStream.SafeFileHandle,
                dumpType,
                ExceptionParam: null,
                UserStreamParam: null,
                CallbackParam: null);

            if (result)
            {
                var sizeMB = new FileInfo(dumpPath).Length / (1024.0 * 1024.0);
                console.MarkupLine($"[red]Dump:[/]      {dumpPath.EscapeMarkup()} ({sizeMB:F1} MB)");
            }
            else
            {
                logger.LogDebug("MiniDumpWriteDump failed for process {PID}.", processId);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to write crash dump: {Message}", ex.Message);
        }
    }

    private static unsafe void CloseHandleSafe(HANDLE handle)
    {
        if (!handle.IsNull && handle.Value != (void*)-1)
        {
            PInvoke.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads STOWED_EXCEPTION_INFORMATION_V2 structures from the exception record's
    /// ExceptionInformation parameters. For stowed exceptions (0xC000027B):
    ///   ExceptionInformation[1] = count of stowed exceptions
    ///   ExceptionInformation[0] = pointer to array of pointers to STOWED_EXCEPTION_INFORMATION_V2
    /// Each stowed exception contains either a binary stack trace (array of instruction pointers
    /// captured at the original throw site) or a text error message.
    /// </summary>
    private unsafe void ReadStowedExceptions(uint processId, in EXCEPTION_RECORD exceptionRecord)
    {
        try
        {
            if (exceptionRecord.NumberParameters < 2)
            {
                return;
            }

            // ExceptionInformation is a fixed-size inline array; use pointer arithmetic to index
            int count;
            nuint arrayPtr;
            fixed (nuint* pInfo = &exceptionRecord.ExceptionInformation._0)
            {
                count = (int)pInfo[1];
                arrayPtr = pInfo[0];
            }

            if (count <= 0 || count > 16 || arrayPtr == 0)
            {
                return;
            }

            using var processHandle = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, processId);

            if (processHandle.IsInvalid)
            {
                return;
            }

            // Read the array of pointers to STOWED_EXCEPTION_INFORMATION_V2
            var pointers = new nuint[count];
            fixed (nuint* pPointers = pointers)
            {
                if (!PInvoke.ReadProcessMemory(processHandle, (void*)arrayPtr,
                    new Span<byte>(pPointers, count * sizeof(nuint)), out _))
                {
                    return;
                }
            }

            // Collect unique HRESULTs and any readable error text or stack traces
            var seenHresults = new HashSet<uint>();

            for (int i = 0; i < count; i++)
            {
                if (pointers[i] == 0)
                {
                    continue;
                }

                ReadSingleStowedException(processHandle, pointers[i], seenHresults);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to read stowed exceptions: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Reads and prints a single STOWED_EXCEPTION_INFORMATION_V2 structure.
    /// Layout (x64): Header(16 bytes) + ResultCode(4) + ExceptionForm:2|ThreadId:30(4) +
    ///   Binary: ExceptionAddress(8) + StackTraceWordSize(4) + StackTraceWords(4) + StackTrace(8)
    ///   Text:   ErrorText(8)
    /// </summary>
    private unsafe void ReadSingleStowedException(SafeHandle processHandle, nuint structPtr, HashSet<uint> seenHresults)
    {
        // Read the structure — header is only 8 bytes (Size + Signature), not 16
        Span<byte> rawData = stackalloc byte[64];
        if (!PInvoke.ReadProcessMemory(processHandle, (void*)structPtr, rawData, out var bytesRead) || bytesRead < 32)
        {
            return;
        }

        fixed (byte* p = rawData)
        {
            // Header: Size(4) + Signature(4) = 8 bytes
            var headerSize = *(uint*)(p + 0);
            var signature = *(uint*)(p + 4);

            if (signature != 0x53453032 && signature != 0x53453031) // "SE02" and "SE01" as uint32
            {
                return;
            }

            // ResultCode at offset 8
            var resultCode = *(uint*)(p + 8);
            // ExceptionForm (2 bits) + ThreadId (30 bits) at offset 12
            var formAndThread = *(uint*)(p + 12);
            var exceptionForm = formAndThread & 0x3;

            if (resultCode != 0 && seenHresults.Add(resultCode))
            {
                // Map common HRESULT values
                string hresultName = resultCode switch
                {
                    0x80004003 => "Invalid pointer",
                    0x80004002 => "No such interface",
                    0x80004001 => "Not implemented",
                    0x80004005 => "Unspecified error",
                    0x80070057 => "Invalid argument",
                    0x80131509 => "Target invocation exception",
                    0x80131604 => "COM exception",
                    0x8007000E => "Out of memory",
                    _ => $"0x{resultCode:X8}",
                };
                console.MarkupLine($"[red]HRESULT:[/]    0x{resultCode:X8} ({hresultName.EscapeMarkup()})");
            }

            if (exceptionForm == 1) // STOWED_EXCEPTION_FORM_TEXT
            {
                // ErrorText pointer at offset 16
                var textPtr = *(nuint*)(p + 16);
                if (textPtr != 0)
                {
                    var textMessage = ReadRemoteWideString(processHandle, textPtr, 512);
                    if (!string.IsNullOrWhiteSpace(textMessage) && IsReadableText(textMessage))
                    {
                        console.MarkupLine($"[red]Error:[/]      {textMessage.EscapeMarkup()}");
                    }
                }
            }
            else if (exceptionForm == 0) // STOWED_EXCEPTION_FORM_BINARY
            {
                // ExceptionAddress at offset 16, StackTraceWordSize at 24,
                // StackTraceWords at 28, StackTrace pointer at 32
                var stackTraceWordSize = *(uint*)(p + 24);
                var stackTraceWords = *(uint*)(p + 28);
                var stackTracePtr = *(nuint*)(p + 32);

                if (stackTraceWords > 0 && stackTraceWords <= 100 && stackTracePtr != 0 &&
                    (stackTraceWordSize == 4 || stackTraceWordSize == 8))
                {
                    var traceSize = (int)(stackTraceWords * stackTraceWordSize);
                    var traceData = traceSize <= 2048 ? stackalloc byte[traceSize] : new byte[traceSize];

                    if (PInvoke.ReadProcessMemory(processHandle, (void*)stackTracePtr, traceData, out var traceRead)
                        && traceRead >= (nuint)traceSize)
                    {
                        console.MarkupLine("[red]Stowed stack (original throw site):[/]");

                        fixed (byte* pTrace = traceData)
                        {
                            var maxFrames = Math.Min((int)stackTraceWords, 20);
                            for (int f = 0; f < maxFrames; f++)
                            {
                                ulong ip = stackTraceWordSize == 8
                                    ? ((ulong*)pTrace)[f]
                                    : ((uint*)pTrace)[f];

                                if (ip == 0)
                                {
                                    break;
                                }

                                var frame = ResolveAddress((nuint)ip);
                                console.MarkupLine($"[red]  {frame.EscapeMarkup()}[/]");
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reads a null-terminated wide (UTF-16) string from the target process.
    /// </summary>
    private static unsafe string? ReadRemoteWideString(SafeHandle processHandle, nuint address, int maxChars)
    {
        var bufferSize = maxChars * 2;
        Span<byte> buffer = bufferSize <= 4096 ? stackalloc byte[bufferSize] : new byte[bufferSize];

        if (!PInvoke.ReadProcessMemory(processHandle, (void*)address, buffer, out var bytesRead) || bytesRead == 0)
        {
            return null;
        }

        var chars = MemoryMarshal.Cast<byte, char>(buffer[..(int)bytesRead]);
        var nullIndex = chars.IndexOf('\0');
        return nullIndex >= 0 ? new string(chars[..nullIndex]) : new string(chars);
    }

    /// <summary>
    /// Checks if a string looks like readable text (not garbled binary data).
    /// </summary>
    private static bool IsReadableText(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        int readable = 0;
        foreach (var c in text)
        {
            if (c >= 0x20 && c < 0x7F)
            {
                readable++;
            }
        }
        return readable > text.Length * 0.5;
    }

    /// <summary>
    /// Resolves a stack frame address to "module!function+offset (file:line)" using dbghelp
    /// symbols and line info, falling back to the module map for module+offset resolution.
    /// </summary>
    private unsafe string ResolveStackFrame(SafeHandle processHandle, ulong address)
    {
        // Allocate SYMBOL_INFO with space for the name (variable-length field)
        const int maxNameLen = 256;
        var bufferSize = sizeof(SYMBOL_INFO) + (maxNameLen - 1) * sizeof(byte);
        var buffer = stackalloc byte[bufferSize];
        var symbolInfo = (SYMBOL_INFO*)buffer;
        symbolInfo->SizeOfStruct = (uint)sizeof(SYMBOL_INFO);
        symbolInfo->MaxNameLen = maxNameLen;

        string result;
        if (PInvoke.SymFromAddr(processHandle, address, out var displacement, symbolInfo))
        {
            var symbolName = new string((sbyte*)&symbolInfo->Name, 0, (int)symbolInfo->NameLen);
            var modulePart = ResolveAddress((nuint)address);
            var moduleBaseName = modulePart.Contains('+') ? modulePart[..modulePart.IndexOf('+')] : modulePart;
            result = displacement > 0
                ? $"{moduleBaseName}!{symbolName}+0x{displacement:X}"
                : $"{moduleBaseName}!{symbolName}";
        }
        else
        {
            result = ResolveAddress((nuint)address);
        }

        // Try to get source file and line number
        if (PInvoke.SymGetLineFromAddr64(processHandle, address, out _, out var lineInfo))
        {
            var fileName = lineInfo.FileName.ToString();
            if (!string.IsNullOrEmpty(fileName))
            {
                // Show just the filename, not the full path
                fileName = Path.GetFileName(fileName);
                result = $"{result}  ({fileName}:{lineInfo.LineNumber})";
            }
        }

        return result;
    }

    /// <summary>
    /// Maps well-known Windows exception/NTSTATUS codes to human-readable names.
    /// This method is internal so it can be reused by RunCommand for exit code interpretation.
    /// </summary>
    internal static string GetExceptionName(uint code) => code switch
    {
        0xC0000005 => "Access Violation",
        0xC00000FD => "Stack Overflow",
        0xC0000094 => "Integer Division By Zero",
        0xC0000017 => "No Memory",
        0xC000001D => "Illegal Instruction",
        0xC0000025 => "Non-Continuable Exception",
        0xC000008C => "Array Bounds Exceeded",
        0xC0000135 => "DLL Not Found",
        0xC0000142 => "DLL Initialization Failed",
        0xC0000409 => "Stack Buffer Overrun",
        0xC0000374 => "Heap Corruption",
        0xC0000602 => "Unknown Software Exception",
        0xC000027B => "Stowed Exception",
        STATUS_BREAKPOINT => "Breakpoint",
        STATUS_SINGLE_STEP => "Single Step",
        0xE06D7363 => "C++ Exception",
        0xE0434352 => "CLR Exception",
        // .NET HRESULT exit codes (process exits with these when CLR terminates)
        0x80131623 => ".NET FailFast (Environment.FailFast called)",
        0x800703E9 => "Stack Overflow (recursion too deep)",
        0x80131506 => ".NET Unhandled Exception",
        0x80131604 => ".NET COM Exception",
        _ => "Exception",
    };
}

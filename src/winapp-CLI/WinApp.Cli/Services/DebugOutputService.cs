// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// The CLI requires Windows 10+; suppress platform compat warnings for Debug APIs.
#pragma warning disable CA1416
// _logWriter lifetime is managed in RunDebugLoopAsync try/finally, not via IDisposable.
#pragma warning disable CA1001

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
internal sealed class DebugOutputService(IAnsiConsole console, ICrashDumpService crashDumpService, ILogger<DebugOutputService> logger) : IDebugOutputService
{
    // Well-known NTSTATUS / exception codes
    private const uint STATUS_BREAKPOINT = 0x80000003;
    private const uint STATUS_SINGLE_STEP = 0x80000004;
    private const uint STATUS_WX86_BREAKPOINT = 0x4000001F;
    private const uint THREAD_NAME_EXCEPTION = 0x406D1388;

    // Set by the debug loop when a crash dump is captured.
    private string? _crashDumpPath;

    // Log writer for verbose debug output (OutputDebugString, first-chance exceptions).
    private StreamWriter? _logWriter;
    private string? _logPath;

    // Saved first-chance exception context — at first-chance time the thread context
    // still points to user code. By second-chance, XAML's FailFast has replaced the stack.
    private byte[]? _savedFirstChanceContext;
    private uint _savedFirstChanceThreadId;
    private int _savedFirstChanceExceptionCode;
    private nuint _savedFirstChanceExceptionAddress;

    /// <inheritdoc/>
    public async Task<int> RunDebugLoopAsync(uint processId, CancellationToken cancellationToken, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null)
    {
        // Create a log file alongside the dump directory for verbose debug output.
        var logDir = Path.Combine(Path.GetTempPath(), "winapp-dumps");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, $"debug-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _logWriter = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };

        try
        {
            // DebugActiveProcess + WaitForDebugEventEx must be called from the same thread,
            // so spin up a dedicated thread via Task.Run.
            var exitCode = await Task.Run(() => RunDebugLoop(processId, cancellationToken), cancellationToken);

            // Close the log writer before analysis appends to the same file.
            _logWriter.Dispose();
            _logWriter = null;

            // After the debug loop ends, analyze the crash dump if one was captured.
            if (_crashDumpPath != null)
            {
                await crashDumpService.AnalyzeDumpAsync(_crashDumpPath, _logPath!, useSymbols, symbolSearchPaths);
            }
            else
            {
                // No crash — show log path so users can find captured debug output.
                console.MarkupLine($"[dim]Full debug log:[/] {_logPath!.EscapeMarkup()}");
            }

            return exitCode;
        }
        finally
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
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

    private int DebugEventLoop(uint processId, CancellationToken cancellationToken)
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
                    PInvoke.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
                    return exitCode;

                case DEBUG_EVENT_CODE.CREATE_PROCESS_DEBUG_EVENT:
                    CloseHandleSafe(debugEvent.u.CreateProcessInfo.hFile);
                    break;

                case DEBUG_EVENT_CODE.LOAD_DLL_DEBUG_EVENT:
                    CloseHandleSafe(debugEvent.u.LoadDll.hFile);
                    break;
            }

            PInvoke.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
        }

        return exitCode;
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
            // Trim trailing newline so log doesn't double-space the output.
            message = message.TrimEnd('\r', '\n');

            // Log file gets everything for detailed investigation.
            _logWriter?.WriteLine($"[Debug] {message}");

            // Console only shows app-specific messages — filter out OS/framework
            // noise from WinUI, COM, DirectX, and other system DLLs.
            if (!IsFrameworkNoise(message))
            {
                console.MarkupLine($"[dim][[Debug]][/] {message.EscapeMarkup()}");
            }
        }
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

        if (firstChance)
        {
            var name = GetExceptionName(code);
            var address = (nuint)exInfo.ExceptionRecord.ExceptionAddress;

            // Log file gets all first-chance exceptions.
            _logWriter?.WriteLine($"First-chance exception: {name} (0x{code:X8}) at 0x{address:X}");

            // Console only shows exceptions meaningful for crash diagnosis.
            // Skip WinUI/COM internal exceptions (0x40080201, 0x04242420, etc.)
            // that are caught and handled during normal framework operation.
            if (code is 0xE0434352 or 0xC0000005 or 0xC00000FD)
            {
                console.MarkupLine($"[yellow]First-chance exception:[/] {name} (0x{code:X8}) at 0x{address:X}");
            }

            // Save thread context for the FIRST critical exception — at first-chance
            // time, the context still points to user code. Later exceptions (CLR wrapping
            // the AV) have already unwound the stack. Only save once per crash sequence.
            if (_savedFirstChanceContext == null &&
                code is 0xC0000005 or 0xC00000FD or 0xE0434352 or 0xE06D7363)
            {
                SaveFirstChanceContext(debugEvent.dwThreadId, code, address);
            }

            // Stack Overflow is always fatal in .NET — no second-chance will follow.
            // Capture the dump immediately on first-chance.
            if (code is 0xC00000FD && _crashDumpPath == null)
            {
                console.MarkupLine($"[red]Crash:[/] {name} (0x{code:X8}) at 0x{address:X}");
                _crashDumpPath = crashDumpService.WriteMiniDump(
                    debugEvent.dwProcessId,
                    _savedFirstChanceContext, _savedFirstChanceThreadId,
                    _savedFirstChanceExceptionCode, _savedFirstChanceExceptionAddress);
            }
        }
        else
        {
            // Second-chance exception — the process is about to crash.
            // Only capture if we don't already have a dump (e.g., Stack Overflow
            // already captured at first-chance time).
            var name = GetExceptionName(code);
            var address = (nuint)exInfo.ExceptionRecord.ExceptionAddress;
            _logWriter?.WriteLine($"Second-chance exception (crash): {name} (0x{code:X8}) at 0x{address:X}");
            console.MarkupLine($"[red]Crash:[/] {name} (0x{code:X8}) at 0x{address:X}");

            if (_crashDumpPath == null)
            {
                _crashDumpPath = crashDumpService.WriteMiniDump(
                    debugEvent.dwProcessId,
                    _savedFirstChanceContext, _savedFirstChanceThreadId,
                    _savedFirstChanceExceptionCode, _savedFirstChanceExceptionAddress);
            }
        }

        // Let the target's own exception handling run. For second-chance exceptions
        // (firstChance == false), this causes the OS to terminate the process — correct
        // behavior for a passive listener that doesn't handle exceptions itself.
        continueStatus = NTSTATUS.DBG_EXCEPTION_NOT_HANDLED;
    }

    private static unsafe void CloseHandleSafe(HANDLE handle)
    {
        if (!handle.IsNull && handle.Value != (void*)-1)
        {
            PInvoke.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Captures the faulting thread's context at first-chance time, when it still
    /// points to the user code that caused the exception.
    /// </summary>
    private unsafe void SaveFirstChanceContext(uint threadId, uint code, nuint address)
    {
        using var threadHandle = PInvoke.OpenThread_SafeHandle(
            THREAD_ACCESS_RIGHTS.THREAD_GET_CONTEXT | THREAD_ACCESS_RIGHTS.THREAD_QUERY_INFORMATION,
            false, threadId);

        if (threadHandle.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _logWriter?.WriteLine($"[CrashDump] OpenThread failed for thread {threadId}: error {err}");
            return;
        }

        // CONTEXT must be 16-byte aligned on x64. Allocate on native heap to guarantee alignment.
        var contextSize = sizeof(CONTEXT);
        var pContext = (CONTEXT*)NativeMemory.AlignedAlloc((nuint)contextSize, 16);
        try
        {
            NativeMemory.Clear(pContext, (nuint)contextSize);
            pContext->ContextFlags = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => CONTEXT_FLAGS.CONTEXT_FULL_ARM64,
                _ => CONTEXT_FLAGS.CONTEXT_FULL_AMD64,
            };

            if (PInvoke.GetThreadContext(new HANDLE(threadHandle.DangerousGetHandle()), pContext))
            {
                _savedFirstChanceContext = new byte[contextSize];
                fixed (byte* p = _savedFirstChanceContext)
                {
                    Buffer.MemoryCopy(pContext, p, contextSize, contextSize);
                }
                _savedFirstChanceThreadId = threadId;
                _savedFirstChanceExceptionCode = unchecked((int)code);
                _savedFirstChanceExceptionAddress = address;

                _logWriter?.WriteLine($"[CrashDump] Saved first-chance context for thread {threadId} (0x{code:X8}) at 0x{address:X}");
            }
            else
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _logWriter?.WriteLine($"[CrashDump] GetThreadContext failed for thread {threadId}: error {err}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(pContext);
        }
    }

    private static string GetExceptionName(uint code) => code switch
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
        STATUS_BREAKPOINT => "Breakpoint",
        STATUS_SINGLE_STEP => "Single Step",
        0xE06D7363 => "C++ Exception",
        0xE0434352 => "CLR Exception",
        _ => "Exception",
    };

    /// <summary>
    /// Returns true if the debug message is internal OS/framework noise
    /// rather than an app-specific debug message worth showing on the console.
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

        // Windows SDK build paths (Azure DevOps build agent)
        if (message.StartsWith("C:\\__w\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Framework DLL WIL/HRESULT trace format: "DllName.dll!0x..." or "DllName.dll!FuncName"
        if (IsFrameworkDllTrace(message))
        {
            return true;
        }

        // Common framework HRESULT noise
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
        if (bangIndex < 5)
        {
            return false;
        }

        var beforeBang = message.AsSpan(0, bangIndex);
        if (!beforeBang.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
}

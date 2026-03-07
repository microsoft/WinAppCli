// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// The CLI requires Windows 10+; suppress platform compat warnings for Debug APIs.
#pragma warning disable CA1416

using Microsoft.Extensions.Logging;
using Spectre.Console;
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
internal sealed class DebugOutputService(IAnsiConsole console, ILogger<DebugOutputService> logger) : IDebugOutputService
{
    // Well-known NTSTATUS / exception codes
    private const uint STATUS_BREAKPOINT = 0x80000003;
    private const uint STATUS_SINGLE_STEP = 0x80000004;
    private const uint STATUS_WX86_BREAKPOINT = 0x4000001F;
    private const uint THREAD_NAME_EXCEPTION = 0x406D1388;

    /// <inheritdoc/>
    public Task<int> RunDebugLoopAsync(uint processId, CancellationToken cancellationToken)
    {
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
            // Trim trailing newline so Spectre doesn't double-space the output.
            message = message.TrimEnd('\r', '\n');
            console.MarkupLine($"[dim][[Debug]][/] {message.EscapeMarkup()}");
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
            console.MarkupLine($"[yellow]First-chance exception:[/] {name} (0x{code:X8}) at 0x{address:X}");
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
}

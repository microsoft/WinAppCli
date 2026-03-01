// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace WinApp.Cli.Services;

[SupportedOSPlatform("windows10.0.10240")]
internal class DebugOutputService : IDebugOutputService
{
    // DBG_CONTINUE: continue execution after debug event
    private static readonly NTSTATUS DBG_CONTINUE = new(0x00010002);
    // DBG_EXCEPTION_NOT_HANDLED: pass exception to the process's exception handlers
    private static readonly NTSTATUS DBG_EXCEPTION_NOT_HANDLED = new(unchecked((int)0x80010001));

    // Well-known exception codes to suppress (internal debugger/runtime notifications)
    private const uint EXCEPTION_BREAKPOINT = 0x80000003;
    private const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    private const uint CLR_NOTIFICATION_EXCEPTION = 0x04242420;
    private const uint WINRT_ORIGINATE_ERROR = 0x40080201;

    /// <inheritdoc />
    public Task RunDebugEventLoopAsync(uint processId, TextWriter output, bool captureDebug = true, bool captureDebugAll = false, bool captureExceptions = true, CancellationToken cancellationToken = default)
    {
        // The debug event loop must run on its own dedicated thread because
        // WaitForDebugEventEx requires the same thread that called DebugActiveProcess.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                if (!PInvoke.DebugActiveProcess(processId))
                {
                    var error = Marshal.GetLastWin32Error();
                    tcs.SetException(new InvalidOperationException(
                        $"Failed to attach debugger to process {processId} (error 0x{error:X8}). " +
                        "Ensure no other debugger is attached."));
                    return;
                }

                HANDLE processHandle = HANDLE.Null;
                bool processExited = false;

                try
                {
                    while (!processExited && !cancellationToken.IsCancellationRequested)
                    {
                        if (!PInvoke.WaitForDebugEventEx(out var debugEvent, 100))
                        {
                            continue;
                        }

                        var continueStatus = DBG_CONTINUE;

                        switch (debugEvent.dwDebugEventCode)
                        {
                            case DEBUG_EVENT_CODE.CREATE_PROCESS_DEBUG_EVENT:
                                processHandle = debugEvent.u.CreateProcessInfo.hProcess;
                                // Close handles we don't need
                                if (!debugEvent.u.CreateProcessInfo.hFile.IsNull)
                                {
                                    PInvoke.CloseHandle(debugEvent.u.CreateProcessInfo.hFile);
                                }
                                if (!debugEvent.u.CreateProcessInfo.hThread.IsNull)
                                {
                                    PInvoke.CloseHandle(debugEvent.u.CreateProcessInfo.hThread);
                                }
                                break;

                            case DEBUG_EVENT_CODE.OUTPUT_DEBUG_STRING_EVENT:
                                if (captureDebug || captureDebugAll)
                                {
                                    var message = ReadDebugString(processHandle, debugEvent.u.DebugString);
                                    if (!string.IsNullOrEmpty(message))
                                    {
                                        if (captureDebugAll || !IsSystemDebugNoise(message))
                                        {
                                            output.WriteLine($"[DEBUG] {message}");
                                        }
                                    }
                                }
                                break;

                            case DEBUG_EVENT_CODE.EXCEPTION_DEBUG_EVENT:
                                var exCode = debugEvent.u.Exception.ExceptionRecord.ExceptionCode;
                                bool firstChance = debugEvent.u.Exception.dwFirstChance != 0;

                                // Suppress internal runtime exceptions; show user-visible ones if filter allows
                                if (captureExceptions && exCode != EXCEPTION_BREAKPOINT && exCode != EXCEPTION_SINGLE_STEP && exCode != CLR_NOTIFICATION_EXCEPTION && exCode != WINRT_ORIGINATE_ERROR)
                                {
                                    var label = firstChance ? "First-chance" : "Unhandled";
                                    output.WriteLine($"[EXCEPTION] {label} exception 0x{exCode:X8}");
                                }

                                // Let the process handle first-chance exceptions
                                if (firstChance)
                                {
                                    continueStatus = DBG_EXCEPTION_NOT_HANDLED;
                                }
                                break;

                            case DEBUG_EVENT_CODE.EXIT_PROCESS_DEBUG_EVENT:
                                processExited = true;
                                break;

                            case DEBUG_EVENT_CODE.LOAD_DLL_DEBUG_EVENT:
                                // Close the DLL file handle — we don't need it
                                if (!debugEvent.u.LoadDll.hFile.IsNull)
                                {
                                    PInvoke.CloseHandle(debugEvent.u.LoadDll.hFile);
                                }
                                break;

                            case DEBUG_EVENT_CODE.CREATE_THREAD_DEBUG_EVENT:
                                // Close the thread handle — we don't need it
                                if (!debugEvent.u.CreateThread.hThread.IsNull)
                                {
                                    PInvoke.CloseHandle(debugEvent.u.CreateThread.hThread);
                                }
                                break;
                        }

                        PInvoke.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
                    }
                }
                finally
                {
                    if (!processExited)
                    {
                        // Detach cleanly if we're stopping early
                        PInvoke.DebugActiveProcessStop(processId);
                    }
                    // Close the process handle we got from CREATE_PROCESS_DEBUG_EVENT
                    if (!processHandle.IsNull)
                    {
                        PInvoke.CloseHandle(processHandle);
                    }
                }

                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"DebugOutput-{processId}"
        };

        thread.Start();
        return tcs.Task;
    }

    private static unsafe string ReadDebugString(HANDLE processHandle, OUTPUT_DEBUG_STRING_INFO debugString)
    {
        if (processHandle.IsNull)
        {
            return string.Empty;
        }

        int length = debugString.nDebugStringLength;
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new byte[length];

        fixed (byte* pBuffer = buffer)
        {
            if (!PInvoke.ReadProcessMemory(processHandle, debugString.lpDebugStringData, pBuffer, (nuint)length, null))
            {
                return string.Empty;
            }
        }

        string result;
        if (debugString.fUnicode != 0)
        {
            result = Encoding.Unicode.GetString(buffer, 0, length);
        }
        else
        {
            result = Encoding.ASCII.GetString(buffer, 0, length);
        }

        // Trim null terminators and trailing whitespace
        return result.TrimEnd('\0', '\r', '\n');
    }

    // Known system source path prefixes in OutputDebugString from Windows/runtime components
    private static readonly string[] SystemDebugPrefixes =
    [
        @"onecore\",
        @"onecoreuap\",
        @"C:\__w\",           // Windows App Runtime build agent paths
        @"minkernel\",
        @"dxg\",
        @"shell\",
        @"windows\",
    ];

    /// <summary>
    /// Detects OutputDebugString messages from system/runtime components by source path prefix.
    /// These messages typically have the format: "source\path.cpp(line)\module.dll!addr: ..."
    /// App-level Debug.WriteLine produces plain text with no source prefix.
    /// </summary>
    private static bool IsSystemDebugNoise(string message)
    {
        foreach (var prefix in SystemDebugPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

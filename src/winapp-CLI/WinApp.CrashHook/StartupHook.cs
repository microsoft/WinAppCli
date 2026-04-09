// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// DOTNET_STARTUP_HOOKS entry point. The CLR loads this assembly and calls
// StartupHook.Initialize() before the app's Main() method. We subscribe to
// FirstChanceException and write exception details to a named pipe that the
// winapp CLI reads on the other end.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;

internal class StartupHook
{
    private static NamedPipeClientStream? _pipe;
    private static StreamWriter? _writer;
    private static readonly object _lock = new();

    public static void Initialize()
    {
        // The pipe name is passed via env var (--with-alias) or runtimeconfig.json (AUMID).
        // AppContext.GetData reads configProperties from runtimeconfig.json.
        var pipeName = Environment.GetEnvironmentVariable("WINAPP_CRASH_PIPE")
            ?? AppContext.GetData("WINAPP_CRASH_PIPE") as string;

        if (string.IsNullOrEmpty(pipeName))
        {
            return;
        }

        try
        {
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            _pipe.Connect(3000); // 3 second timeout
            _writer = new StreamWriter(_pipe) { AutoFlush = true };

            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }
        catch
        {
            // If the pipe isn't available, silently degrade — don't crash the app.
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        try
        {
            var ex = e.Exception;
            var exType = ex.GetType().FullName ?? ex.GetType().Name;
            var message = ex.Message ?? "";

            // Capture the managed stack trace with source file:line info.
            // Use the current thread's call stack (not ex.StackTrace) because at
            // FirstChanceException time, the exception's stack trace may not be
            // fully populated yet. We walk frames and filter out hook/runtime code.
            string stackTrace;
            try
            {
                var st = new StackTrace(fNeedFileInfo: true);
                // Find the first frame that's not in this hook or System/runtime code
                var sb = new System.Text.StringBuilder();
                bool foundUserCode = false;
                for (int i = 0; i < st.FrameCount; i++)
                {
                    var frame = st.GetFrame(i);
                    if (frame == null)
                    {
                        continue;
                    }

                    var method = frame.GetMethod();
                    if (method == null)
                    {
                        continue;
                    }

                    var declType = method.DeclaringType?.FullName ?? "";

                    // Skip frames from this hook class and CLR internals
                    if (declType == "StartupHook" ||
                        declType.StartsWith("System.", StringComparison.Ordinal) ||
                        declType.StartsWith("Internal.", StringComparison.Ordinal))
                    {
                        if (foundUserCode)
                        {
                            break; // Stop after user code ends
                        }
                        continue;
                    }

                    foundUserCode = true;
                    var fileName = frame.GetFileName();
                    var lineNumber = frame.GetFileLineNumber();

                    sb.Append("   at ");
                    sb.Append(declType);
                    sb.Append('.');
                    sb.Append(method.Name);
                    sb.Append('(');
                    var parameters = method.GetParameters();
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        if (p > 0) { sb.Append(", "); }
                        sb.Append(parameters[p].ParameterType.Name);
                        sb.Append(' ');
                        sb.Append(parameters[p].Name);
                    }
                    sb.Append(')');

                    if (fileName != null && lineNumber > 0)
                    {
                        sb.Append(" in ");
                        sb.Append(Path.GetFileName(fileName));
                        sb.Append(":line ");
                        sb.Append(lineNumber);
                    }

                    sb.AppendLine();
                }

                stackTrace = sb.Length > 0 ? sb.ToString() : (ex.StackTrace ?? "");
            }
            catch
            {
                stackTrace = ex.StackTrace ?? "";
            }

            // Protocol: each exception is a block of lines terminated by "---END---"
            // Type: <fully qualified type name>
            // Message: <exception message>
            // HResult: <hex hresult>
            // Stack: <multi-line stack trace>
            // ---END---
            lock (_lock)
            {
                if (_writer == null)
                {
                    return;
                }

                try
                {
                    _writer.WriteLine($"Type: {exType}");
                    _writer.WriteLine($"Message: {message}");
                    _writer.WriteLine($"HResult: 0x{unchecked((uint)ex.HResult):X8}");
                    _writer.WriteLine("Stack:");
                    if (!string.IsNullOrWhiteSpace(stackTrace))
                    {
                        _writer.WriteLine(stackTrace);
                    }
                    _writer.WriteLine("---END---");
                }
                catch
                {
                    // Pipe broken — stop writing and clean up
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }
        catch
        {
            // Never throw from a FirstChanceException handler — that causes infinite recursion.
        }
    }
}

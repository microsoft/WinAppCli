// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma warning disable CA1416

using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Utilities;
using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;
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
    public async Task AnalyzeDumpAsync(string dumpPath, string logPath, bool useSymbols = false)
    {
        console.MarkupLine("[dim]Analyzing crash dump...[/]");

        try
        {
            var (summary, details) = await Task.Run(() => AnalyzeWithClrMD(dumpPath));

            // ClrMD found managed exception — no need for native fallback
            if (!string.IsNullOrWhiteSpace(summary))
            {
                if (!string.IsNullOrWhiteSpace(details))
                {
                    await File.AppendAllTextAsync(logPath,
                        $"\n\n=== Crash Analysis ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\n{details}\n");
                }

                console.WriteLine();
                console.MarkupLine("[red]========== CRASH DETECTED ==========[/]");
                console.WriteLine();
                console.MarkupLine("[red][[CRASH ANALYSIS]][/]");
                console.WriteLine(summary);
                console.MarkupLine("[red]=====================================[/]");
                console.MarkupLine($"[dim]Crash dump:[/] {dumpPath.EscapeMarkup()}");
                console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
                return;
            }

            // No managed info — fallback to DbgEng for native stack trace
            if (useSymbols)
            {
                console.MarkupLine("[dim]Downloading symbols (first run may take a few minutes)...[/]");
            }

            var (nativeSummary, nativeDetails) = await Task.Run(() => AnalyzeWithDbgEng(dumpPath, useSymbols));

            var allDetails = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(details))
            {
                allDetails.AppendLine(details);
            }

            if (!string.IsNullOrWhiteSpace(nativeDetails))
            {
                allDetails.AppendLine(nativeDetails);
            }

            if (allDetails.Length > 0)
            {
                await File.AppendAllTextAsync(logPath,
                    $"\n\n=== Crash Analysis ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\n{allDetails}\n");
            }

            console.WriteLine();
            console.MarkupLine("[red]========== CRASH DETECTED ==========[/]");

            if (!string.IsNullOrWhiteSpace(nativeSummary))
            {
                console.WriteLine();
                console.MarkupLine("[red][[CRASH ANALYSIS (native)]][/]");
                console.WriteLine(nativeSummary);
                console.MarkupLine("[red]=====================================[/]");
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

    private static (string Summary, string Details) AnalyzeWithDbgEng(string dumpPath, bool useSymbols)
    {
        // Use system32's dbgeng.dll — available on every Windows machine.
        var dbgengPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using IDisposable dbgeng = IDebugClient.Create(dbgengPath);

        IDebugClient client = (IDebugClient)dbgeng;
        IDebugControl control = (IDebugControl)dbgeng;

        var hr = client.OpenDumpFile(dumpPath);
        if (hr < 0)
        {
            return (string.Empty, $"DbgEng failed to open dump: HRESULT 0x{(uint)hr:X8}");
        }

        hr = control.WaitForEvent(TimeSpan.FromSeconds(30));
        if (hr < 0)
        {
            return (string.Empty, $"DbgEng WaitForEvent failed: HRESULT 0x{(uint)hr:X8}");
        }

        var output = new StringBuilder();
        void CaptureOutput(Action action)
        {
            output.Clear();
            using var holder = new DbgEngOutputHolder(client, DEBUG_OUTPUT.ALL);
            holder.OutputReceived += (text, _) => output.Append(text);
            action();
        }

        // First pass: get stack trace without symbols
        CaptureOutput(() =>
        {
            control.Execute(DEBUG_OUTCTL.THIS_CLIENT, ".ecxr", DEBUG_EXECUTE.DEFAULT);
            control.Execute(DEBUG_OUTCTL.THIS_CLIENT, "kp 20", DEBUG_EXECUTE.DEFAULT);
        });

        var stackOutput = output.ToString();

        // If --symbols, download PDBs for modules on the stack, then re-run
        if (useSymbols)
        {
            var symbolCachePath = Path.Combine(Path.GetTempPath(), "symbols");
            var downloaded = DownloadSymbolsForStack(stackOutput, control, client, symbolCachePath);
            if (downloaded > 0)
            {
                IDebugSymbols symbols = (IDebugSymbols)dbgeng;
                // Prepend local cache path — system32's dbgeng can load PDBs from
                // local directories but doesn't support srv*/cache* (requires symsrv.dll).
                // Preserve existing path so dbgeng can still find PDBs next to the DLLs.
                var existingPath = symbols.SymbolPath ?? "";
                symbols.SymbolPath = existingPath.Length > 0
                    ? $"{symbolCachePath};{existingPath}"
                    : symbolCachePath;

                CaptureOutput(() =>
                {
                    control.Execute(DEBUG_OUTCTL.THIS_CLIENT, ".reload /f", DEBUG_EXECUTE.DEFAULT);
                    control.Execute(DEBUG_OUTCTL.THIS_CLIENT, ".ecxr", DEBUG_EXECUTE.DEFAULT);
                    control.Execute(DEBUG_OUTCTL.THIS_CLIENT, "kp 20", DEBUG_EXECUTE.DEFAULT);
                });
                stackOutput = output.ToString();
            }
        }

        var summary = ExtractNativeStackSummary(stackOutput);
        return (summary, $"Native Stack (DbgEng):\n{stackOutput}");
    }

    /// <summary>
    /// Downloads PDB symbols for modules that appear in the native stack trace.
    /// Reads PE headers from the original DLL on disk to get the PDB GUID,
    /// then downloads from Microsoft Symbol Server.
    /// </summary>
    private static int DownloadSymbolsForStack(string stackOutput, IDebugControl control, IDebugClient client, string cachePath)
    {
        // Extract unique module names from stack (e.g., "Microsoft_UI_Xaml" from "Microsoft_UI_Xaml+0x3e503")
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stackOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            var bangIdx = trimmed.IndexOf('!');
            var plusIdx = trimmed.IndexOf('+');

            // "Module!Function+0x..." or "Module+0x..."
            if (plusIdx > 0)
            {
                var start = trimmed.LastIndexOf(' ', bangIdx > 0 ? bangIdx : plusIdx) + 1;
                var end = bangIdx > 0 ? bangIdx : plusIdx;
                if (end > start)
                {
                    var name = trimmed[start..end];
                    // Skip addresses (hex strings) and empty names
                    if (name.Length > 0 && !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                        && !name.Contains('`'))
                    {
                        modules.Add(name);
                    }
                }
            }
        }

        if (modules.Count == 0)
        {
            return 0;
        }

        // For each module, get its DLL path via DbgEng, then read PE header from disk
        var downloaded = 0;
        using var http = new HttpClient();

        foreach (var moduleName in modules)
        {
            try
            {
                // Get module file path from DbgEng
                var modOutput = new StringBuilder();
                using (var holder = new DbgEngOutputHolder(client, DEBUG_OUTPUT.ALL))
                {
                    holder.OutputReceived += (text, _) => modOutput.Append(text);
                    control.Execute(DEBUG_OUTCTL.THIS_CLIENT, $"lmvm {moduleName}", DEBUG_EXECUTE.DEFAULT);
                }

                // Parse "Image path: C:\...\Module.dll" from lmvm output
                var dllPath = ExtractImagePath(modOutput.ToString());
                if (dllPath == null || !File.Exists(dllPath))
                {
                    continue;
                }

                // Read PE CodeView entry to get PDB GUID
                using var stream = File.OpenRead(dllPath);
                using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
                var debugEntries = peReader.ReadDebugDirectory();

                foreach (var entry in debugEntries)
                {
                    if (entry.Type != System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView)
                    {
                        continue;
                    }

                    var cv = peReader.ReadCodeViewDebugDirectoryData(entry);
                    var pdbName = Path.GetFileName(cv.Path);
                    var sig = cv.Guid.ToString("N").ToUpperInvariant() + cv.Age;
                    var localPdb = Path.Combine(cachePath, pdbName, sig, pdbName);

                    // Already cached?
                    if (File.Exists(localPdb))
                    {
                        downloaded++;
                        break;
                    }

                    // Download from Microsoft Symbol Server
                    var url = $"https://msdl.microsoft.com/download/symbols/{pdbName}/{sig}/{pdbName}";
                    using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
                    if (!response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(localPdb)!);
                    using var pdbStream = response.Content.ReadAsStream();
                    using var fileStream = File.Create(localPdb);
                    pdbStream.CopyTo(fileStream);
                    downloaded++;
                    break;
                }
            }
            catch
            {
                // Skip modules we can't process
            }
        }

        return downloaded;
    }

    private static string? ExtractImagePath(string lmvmOutput)
    {
        foreach (var line in lmvmOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Image path:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Image path:".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a concise stack summary from DbgEng kp output for terminal display.
    /// </summary>
    private static string ExtractNativeStackSummary(string output)
    {
        var result = new StringBuilder();
        var lines = output.Split('\n');
        var inStack = false;
        var frameCount = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // kp output starts with "Child-SP" header
            if (line.Contains("Child-SP"))
            {
                inStack = true;
                result.AppendLine("Stack:");
                continue;
            }

            if (!inStack || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (frameCount >= 15)
            {
                result.AppendLine("  ... (more frames in log)");
                break;
            }

            // Find call site by looking for Module!Function or Module+offset pattern
            var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            var callSite = parts.FirstOrDefault(p => p.Contains('!') || p.Contains('+'));
            if (callSite != null)
            {
                // Strip parameters: cut at first '('
                var parenIdx = callSite.IndexOf('(');
                if (parenIdx > 0)
                {
                    callSite = callSite[..parenIdx];
                }

                result.AppendLine($"  {callSite}");
                frameCount++;
            }
        }

        return result.ToString().Trim();
    }
}

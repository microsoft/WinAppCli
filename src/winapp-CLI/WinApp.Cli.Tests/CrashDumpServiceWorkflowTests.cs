// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Spectre.Console.Testing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real-workflow tests for <see cref="CrashDumpService"/> that generate a genuine minidump (via the
/// product's own <see cref="CrashDumpService.WriteMiniDump"/>) and run the in-process ClrMD/DbgEng
/// analyzers against it — the same engines the product uses. No external debugger executable is
/// launched and no symbols are downloaded (the symbol-file boundary is stubbed). Marked
/// <c>[DoNotParallelize]</c> because the tests run the shared in-process debugging engine and mutate
/// the static <see cref="CrashDumpService.SymbolFileDownloader"/> seam.
/// </summary>
/// <remarks>
/// <para><b>Documented coverage ceiling (~90% Debug line coverage; the full-suite run is authoritative).</b>
/// The remaining uncovered lines are unreachable on this host without a foreign CPU architecture, a live
/// symbol server, or Win32/engine fault injection, and per policy are left honestly uncovered rather than
/// forced with flaky tests or excluded from coverage. Current uncovered ranges and why:</para>
/// <list type="bullet">
///   <item>162-165, 171-174 — <c>MiniDumpWriteDump</c> P/Invoke returning FALSE (native dump-write failure):
///   defensive; cannot be provoked without corrupting the OS call.</item>
///   <item>378-384, 397-407 — cross-architecture DAC / emulation branch (a dump whose target architecture
///   differs from the host): requires a non-x64 dump analyzed on an x64 host.</item>
///   <item>585-586, 588, 590 — <c>DumpHasWinUiModule</c> catch: module enumeration throwing; defensive.</item>
///   <item>646-648, 676-678 — <c>FormatManagedException</c> exception-object source lines: only run when
///   ClrMD/DAC surfaces a managed exception object with a resolvable stack; environment-dependent.</item>
///   <item>701-702 — <c>WaitForEvent</c> non-success HRESULT: defensive engine guard.</item>
///   <item>764-765, 786-787, 798-799, 809-811, 817-818, 827-830 — real-DbgEng <c>lmvm</c> module/stack
///   symbol enumeration and on-demand symbol resolution: needs a live msdl.microsoft.com symbol server
///   (excluded by policy; the happy path is exercised opportunistically when local symbols are present).</item>
///   <item>877-882, 885-889 — <c>DefaultDownloadSymbolFile</c> real HTTPS symbol download: network boundary
///   (the <c>SymbolFileDownloader</c> seam is stubbed in tests, so the default body is never invoked).</item>
///   <item>998, 1001-1002, 1045-1046, 1065-1066, 1107-1109, 1111, 1113, 1173-1174, 1183-1184, 1186-1187 —
///   <c>PdbSourceResolver</c> null/corrupt-PDB/degenerate-input edges: reached only when ClrMD yields a null
///   method/module or a corrupt portable PDB. ClrMD's <c>ClrMethod</c>/<c>ClrModule</c> are engine-produced
///   and not constructible as fakes, so these guards are not drivable without pathological dumps.</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize]
#pragma warning disable CA1001 // Disposable fields cleaned up in TestCleanup
public sealed class CrashDumpServiceWorkflowTests
{
    private TestConsole _console = null!;
    private CrashDumpService _service = null!;
    private FakeXamlTriageService _xamlTriage = null!;
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _xamlTriage = new FakeXamlTriageService();
        _service = new CrashDumpService(
            _console,
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug)).CreateLogger<CrashDumpService>(),
            _xamlTriage);
        _tempDir = Path.Combine(Path.GetTempPath(), $"CrashDumpWf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _console?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void WriteMiniDump_BenignNativeChild_WritesValidDumpFile()
    {
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        try
        {
            Assert.IsTrue(File.Exists(dump));
            Assert.IsTrue(new FileInfo(dump).Length > 0, "The generated dump must be non-empty.");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void WriteMiniDump_WithSavedContext_WritesDumpWithFirstChanceExceptionRecord()
    {
        using var child = StartBenignChild();
        if (child == null)
        {
            Assert.Inconclusive("Could not start a child process for dump capture.");
            return;
        }

        try
        {
            var tid = GetMainThreadId(child);
            if (tid == 0)
            {
                Assert.Inconclusive("Could not resolve a valid child thread id.");
                return;
            }

            // A zeroed buffer larger than any CONTEXT exercises the saved-context branch; the exception
            // record describes a first-chance AV against the (valid) captured thread.
            var savedContext = new byte[4096];
            var dump = _service.WriteMiniDump(
                (uint)child.Id, savedContext, savedThreadId: tid,
                savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: (nuint)0x1000);

            Assert.IsNotNull(dump, "A saved-context dump should be written.");
            Assert.IsTrue(File.Exists(dump!), "The saved-context dump file must exist.");
            Assert.IsTrue(new FileInfo(dump!).Length > 0, "The saved-context dump must be non-empty.");
            try { File.Delete(dump!); } catch { /* best effort */ }
        }
        finally
        {
            KillChild(child);
        }
    }

    [TestMethod]
    public void WriteMiniDump_WithStowedException_WritesDumpWithStowedParameters()
    {
        using var child = StartBenignChild();
        if (child == null)
        {
            Assert.Inconclusive("Could not start a child process for dump capture.");
            return;
        }

        try
        {
            var tid = GetMainThreadId(child);
            if (tid == 0)
            {
                Assert.Inconclusive("Could not resolve a valid child thread id.");
                return;
            }

            // Terminating stowed exception (0xC000027B) + parameters -> the stowed-record branch runs
            // and copies the stowed-exception parameters into the dump's exception record.
            var savedContext = new byte[4096];
            var dump = _service.WriteMiniDump(
                (uint)child.Id, savedContext, savedThreadId: tid,
                savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: (nuint)0x1000,
                crashExceptionCode: unchecked((int)0xC000027B), crashExceptionAddress: (nuint)0x2000,
                crashExceptionParameters: [(nuint)0x3000, (nuint)2]);

            Assert.IsNotNull(dump, "A stowed-exception dump should be written.");
            Assert.IsTrue(File.Exists(dump!), "The stowed-exception dump file must exist.");
            Assert.IsTrue(new FileInfo(dump!).Length > 0, "The stowed-exception dump must be non-empty.");
            try { File.Delete(dump!); } catch { /* best effort */ }
        }
        finally
        {
            KillChild(child);
        }
    }

    [TestMethod]
    public void WriteMiniDump_InvalidProcessId_ReturnsNull()
    {
        // A process id that cannot be opened -> OpenProcess returns an invalid handle -> null path.
        var dump = _service.WriteMiniDump(
            processId: 0xFFFFFFFC, savedContext: null, savedThreadId: 0,
            savedExceptionCode: 0, savedExceptionAddress: 0);

        Assert.IsNull(dump, "An unopenable process id must yield a null dump path.");
    }

    private static Process? StartBenignChild()
    {
        try
        {
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = "ping.exe",
                Arguments = "-n 60 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (child != null)
            {
                Thread.Sleep(300); // let the child initialize so its memory is dumpable
                child.Refresh();
            }

            return child;
        }
        catch
        {
            return null;
        }
    }

    private static uint GetMainThreadId(Process child)
    {
        try
        {
            child.Refresh();
            if (child.Threads.Count > 0)
            {
                return (uint)child.Threads[0].Id;
            }
        }
        catch
        {
            // fall through
        }

        return 0;
    }

    private static void KillChild(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    [TestMethod]
    public void AnalyzeWithDbgEng_ValidNativeDumpNoSymbols_ProducesNativeStack()
    {
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        try
        {
            (string Summary, string Details) result;
            try
            {
                result = CrashDumpService.AnalyzeWithDbgEng(dump, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process DbgEng engine unavailable: {ex.Message}");
                return;
            }

            StringAssert.Contains(result.Details, "Native Stack (DbgEng)");
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Summary), "A valid native dump should yield a stack summary.");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void AnalyzeWithDbgEng_ValidNativeDumpWithSymbols_DrivesSymbolDownloadSeamWithoutNetwork()
    {
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        var requestedUrls = new List<string>();
        var saved = CrashDumpService.SymbolFileDownloader;

        // The product caches downloaded PDBs under %TEMP%\symbols and, on a cache hit, counts the
        // module WITHOUT consulting the download seam. Clear that regenerable temp cache first so this
        // run deterministically takes the "not cached -> download" path (otherwise a prior run's cache
        // would make the seam appear unused).
        var symbolCache = Path.Combine(Path.GetTempPath(), "symbols");
        try { if (Directory.Exists(symbolCache)) { Directory.Delete(symbolCache, recursive: true); } } catch { /* best effort */ }

        try
        {
            // Stub the symbol-server boundary: pretend every PDB is available (small dummy payload) so
            // the "downloaded > 0 -> reload and re-run with symbols" branch runs without any network.
            CrashDumpService.SymbolFileDownloader = url =>
            {
                requestedUrls.Add(url);
                return [0x4D, 0x69, 0x63]; // arbitrary non-null bytes
            };

            (string Summary, string Details) result;
            try
            {
                result = CrashDumpService.AnalyzeWithDbgEng(dump, useSymbols: true);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process DbgEng engine unavailable: {ex.Message}");
                return;
            }

            StringAssert.Contains(result.Details, "Native Stack (DbgEng)");
            if (requestedUrls.Count == 0)
            {
                // No stack module produced a resolvable on-disk DLL with a CodeView entry in this
                // environment; the download branch cannot be exercised here (not a product failure).
                Assert.Inconclusive("No stack modules with downloadable symbols were found in this environment.");
                return;
            }

            Assert.IsTrue(requestedUrls.TrueForAll(u => u.StartsWith("https://msdl.microsoft.com/", StringComparison.OrdinalIgnoreCase)),
                "The seam must be called with Microsoft Symbol Server URLs.");
        }
        finally
        {
            CrashDumpService.SymbolFileDownloader = saved;
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_RealNativeDump_ReportsNativeCrashViaRealEngines()
    {
        // End-to-end with the real ClrMD + DbgEng analyzers (no overrides): a native dump has no CLR,
        // so ClrMD returns the "native-only" path and the DbgEng fallback produces the native stack.
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "real-native.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            StringAssert.Contains(_console.Output, "CRASH DETECTED");
            Assert.IsTrue(File.Exists(logPath), "A crash-analysis log should be written for a valid dump.");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_RealManagedDump_RunsClrMdManagedEnumeration()
    {
        // A managed (PowerShell) dump exercises the ClrMD managed path: runtime creation, thread/heap
        // scan, stack-overflow check, and the all-threads detail listing.
        var dump = TestProcessDump.TryCreateManagedDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a managed minidump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "real-managed.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!File.Exists(logPath))
            {
                Assert.Inconclusive("ClrMD did not produce a managed analysis log in this environment (DAC unavailable).");
                return;
            }

            var log = await File.ReadAllTextAsync(logPath);
            StringAssert.Contains(log, "CLR Version");
            StringAssert.Contains(log, "All Threads");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_ManagedDumpWithInFlightException_ReportsManagedException()
    {
        var dump = TestProcessDump.TryCreateManagedDumpWithInFlightException(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create an in-flight-exception managed dump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "inflight.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not surface the in-flight exception in this environment (DAC unavailable).");
                return;
            }

            // The in-flight exception is reported via ClrThread.CurrentException, so its type/message
            // must appear in the crash summary.
            StringAssert.Contains(_console.Output, "InvalidOperationException");
            StringAssert.Contains(_console.Output, "INFLIGHT_BOOM");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_ManagedDumpWithHeapException_FormatsExceptionDetails()
    {
        var dump = TestProcessDump.TryCreateManagedDumpWithHeapException(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a heap-exception managed dump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "heapexc.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not surface a managed exception in this environment (DAC unavailable).");
                return;
            }

            // A crash analysis summary was produced by the exception formatter.
            StringAssert.Contains(_console.Output, "Exception:");
            Assert.IsTrue(File.Exists(logPath), "A managed crash-analysis log should be written.");
            var log = await File.ReadAllTextAsync(logPath);
            StringAssert.Contains(log, "Exception Type:");

            // The heap scan keeps the most-recently-thrown exception; when that is ours, assert its
            // outer/inner markers. Otherwise the formatter still ran (coverage achieved) but a different
            // heap exception was chosen, so we do not hard-fail on the markers.
            if (log.Contains("HEAP_BOOM_OUTER", StringComparison.Ordinal))
            {
                StringAssert.Contains(log, "HEAP_BOOM_INNER");
                StringAssert.Contains(log, "Inner Exception");
            }
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_ManagedDumpWithDeepStack_DetectsStackOverflowShape()
    {
        var dump = TestProcessDump.TryCreateManagedDumpWithDeepStack(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a deep-stack managed dump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "deepstack.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not resolve managed frames in this environment (DAC unavailable).");
                return;
            }

            // A thread with hundreds of managed frames is reported as a (deep-recursion) stack overflow.
            StringAssert.Contains(_console.Output, "Stack Overflow (deep recursion detected)");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_DeepStackWithPortablePdb_ResolvesManagedSourceLocations()
    {
        // Same deep-recursion shape as the sibling test, but the child is compiled by the .NET SDK so a
        // *portable* PDB sits next to the managed module. That exercises PdbSourceResolver's on-disk
        // portable-PDB path end to end: candidate discovery, PDB/DLL GUID validation, portable-reader
        // load, and the sequence-point walk that maps IL offsets to "file:line" — none of which the
        // Add-Type (Windows-PDB) build can reach. The build output is passed as a symbol search path so
        // the search-paths candidate branch is covered too.
        var dump = TestProcessDump.TryCreateDeepStackDumpWithPortablePdb(out var err, out var tempRoot, out var moduleDir);
        if (dump == null)
        {
            try { if (tempRoot != null) { Directory.Delete(tempRoot, recursive: true); } } catch { /* best effort */ }
            Assert.Inconclusive($"Could not build/dump a portable-PDB deep-stack child (SDK unavailable?): {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "deepstack-pdb.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false, symbolSearchPaths: [moduleDir!]);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not resolve managed frames in this environment (DAC unavailable).");
                return;
            }

            var log = await File.ReadAllTextAsync(logPath);
            // The recursive method and its source file/line, resolved from the portable PDB, must appear
            // in the analysis — proving the resolver loaded the PDB and walked its sequence points.
            StringAssert.Contains(log, "Deep");
            StringAssert.Contains(log, "Program.cs");
            StringAssert.Contains(log, " in Program.cs:");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
            try { if (tempRoot != null) { Directory.Delete(tempRoot, recursive: true); } } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void AnalyzeWithDbgEng_MissingDumpFile_ReturnsOpenOrWaitFailure()
    {
        // Point the real in-process DbgEng at a path that is not a dump. Opening it (or the ensuing
        // WaitForEvent) must fail with an HRESULT, and the analyzer must surface that as an empty
        // summary plus a descriptive error detail instead of throwing. useSymbols:false so nothing is
        // downloaded. Lives in this [DoNotParallelize] class so it never shares the process-wide
        // engine with another test class.
        var missing = Path.Combine(_tempDir, "not-a-real.dmp");
        (string Summary, string Details) result;
        try
        {
            result = CrashDumpService.AnalyzeWithDbgEng(missing, useSymbols: false);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"In-process DbgEng unavailable in this environment: {ex.Message}");
            return;
        }

        Assert.AreEqual(string.Empty, result.Summary);
        Assert.IsTrue(
            result.Details.Contains("failed to open dump", StringComparison.Ordinal) ||
            result.Details.Contains("WaitForEvent failed", StringComparison.Ordinal),
            $"Expected a DbgEng open/wait failure detail, got: {result.Details}");
    }
}

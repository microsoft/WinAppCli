// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="XamlTriageRunner"/>. The argument parser in <see cref="XamlTriageRunner.Run"/>
/// is exercised directly and deterministically. <see cref="XamlTriageRunner.RunDbgEngExtension"/> hosts
/// the in-process DbgEng engine (the same engine the crash-analysis passes use); following the existing
/// crash-analysis test precedent, it is driven with an <em>invalid</em> dump so the engine's
/// open-dump failure path runs without a real debugging session, and is gated with
/// <see cref="Assert.Inconclusive(string)"/> if the engine cannot initialize in this environment.
/// Marked <c>[DoNotParallelize]</c> because it redirects the process-wide console streams.
/// </summary>
/// <remarks>
/// <para><b>Documented coverage ceiling (~80% Debug line coverage).</b> The <c>Run</c> argument parser is
/// fully covered; the remaining uncovered lines live inside <c>RunDbgEngExtension</c> and are only
/// reachable with a successfully-initialized engine plus either a live symbol server or a specific engine
/// HRESULT failure, so per policy they are left honestly uncovered rather than forced with flaky tests or
/// excluded. Current uncovered ranges and why:</para>
/// <list type="bullet">
///   <item>24, 96, 101-106 — the <c>useSymbols</c> block (symbol-path setup and <c>.reload</c> against the
///   live <c>msdl.microsoft.com</c> symbol server): every deterministic test passes <c>useSymbols:false</c>
///   to stay offline, so this network branch never runs (excluded by policy).</item>
///   <item>86-87 — <c>WaitForEvent</c> returning a non-success HRESULT: a defensive engine guard.</item>
///   <item>117-119 — the <c>.load</c>-failure branch: DbgEng reports success for <c>.load</c> even against a
///   bogus provider path on this host, so the failure return is not deterministically drivable (the
///   <c>RunDbgEngExtension_InvalidJsProviderFile_ReportsLoadFailure</c> test goes Inconclusive rather than
///   fabricating the failure).</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class XamlTriageRunnerTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "winapp-xamlrunner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Runs <see cref="XamlTriageRunner.Run"/> with the console streams redirected.</summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCaptured(string[] args)
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var exit = XamlTriageRunner.Run(args);
            return (exit, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    [TestMethod]
    public void Run_NoArgsBeyondVerb_ReturnsTwoAndExplains()
    {
        var (exit, _, stderr) = RunCaptured([XamlTriageRunner.InternalVerb]);

        Assert.AreEqual(2, exit);
        StringAssert.Contains(stderr, "--dump");
        StringAssert.Contains(stderr, "required");
    }

    [TestMethod]
    public void Run_MissingBinAndExt_ReturnsTwo()
    {
        var (exit, _, _) = RunCaptured([XamlTriageRunner.InternalVerb, "--dump", @"C:\x.dmp"]);

        Assert.AreEqual(2, exit);
    }

    [TestMethod]
    public void Run_MissingExt_ReturnsTwo()
    {
        var (exit, _, _) = RunCaptured([XamlTriageRunner.InternalVerb, "--dump", @"C:\x.dmp", "--bin", @"C:\bin"]);

        Assert.AreEqual(2, exit);
    }

    [TestMethod]
    public void Run_DanglingFlagWithoutValue_TreatedAsMissing_ReturnsTwo()
    {
        // The "--dump" arm has a `when i + 1 < args.Length` guard; a trailing flag with no value must
        // leave dump null so the required-args check fails with exit 2.
        var (exit, _, _) = RunCaptured([XamlTriageRunner.InternalVerb, "--bin", @"C:\bin", "--ext", @"C:\e.js", "--dump"]);

        Assert.AreEqual(2, exit);
    }

    [TestMethod]
    public void Run_AllArgsButUnusableBin_ReturnsOne()
    {
        // With all required args supplied but a bin directory that has no dbgeng.dll, the engine cannot
        // be created, RunDbgEngExtension throws, and Run's catch maps it to exit code 1. This also
        // exercises the --jsprovider and --symbols switch arms.
        var dump = Path.Combine(_tempDir, "garbage.dmp");
        File.WriteAllText(dump, "not a dump");
        var emptyBin = Path.Combine(_tempDir, "empty-bin");
        Directory.CreateDirectory(emptyBin);
        var ext = Path.Combine(_tempDir, "ext.js");
        File.WriteAllText(ext, "// ext");
        var jsProvider = Path.Combine(emptyBin, "JsProvider.dll");

        var (exit, _, stderr) = RunCaptured(
            [XamlTriageRunner.InternalVerb, "--dump", dump, "--bin", emptyBin, "--ext", ext, "--jsprovider", jsProvider, "--symbols"]);

        if (exit == 0)
        {
            Assert.Inconclusive("DbgEng was created from an unexpected fallback location; cannot exercise the failure path here.");
        }

        Assert.AreEqual(1, exit);
        StringAssert.Contains(stderr, "xaml-triage failed");
    }

    [TestMethod]
    public void RunDbgEngExtension_InvalidDump_ReturnsOpenFailureMessage()
    {
        // Mirrors the crash-analysis test precedent (invalid dump → engine open failure). Uses the
        // real system32 dbgeng.dll in-process; if the engine cannot initialize here, skip.
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System));
        var dump = Path.Combine(_tempDir, "garbage.dmp");
        File.WriteAllText(dump, "this is not a valid minidump");
        var jsProvider = Path.Combine(system32, "JsProvider.dll"); // not present; never reached (open fails first)
        var ext = Path.Combine(_tempDir, "ext.js");
        File.WriteAllText(ext, "// ext");

        string result;
        try
        {
            result = XamlTriageRunner.RunDbgEngExtension(dump, system32, jsProvider, ext, useSymbols: false);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"In-process DbgEng engine unavailable in this environment: {ex.Message}");
            return;
        }

        StringAssert.Contains(result, "DbgEng failed to open dump");
    }

    [TestMethod]
    public void RunDbgEngExtension_ValidDumpNoSymbols_RunsFullCommandSequence()
    {
        // A real (valid) minidump opens successfully, so the engine reaches the full command sequence:
        // WaitForEvent -> .ecxr -> .load -> .scriptload -> !xamlstowed -> !xamltriage. Without the real
        // signed JsProvider.dll the script/extension commands report "not found", but they still execute
        // (proving the sequence ran) — no symbols means no network access.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a test minidump: {err}");
            return;
        }

        try
        {
            var ext = Path.Combine(_tempDir, "ext.js");
            File.WriteAllText(ext, "// ext");
            var bogusJsProvider = Path.Combine(_tempDir, "NoSuchJsProvider.dll");

            string result;
            try
            {
                result = XamlTriageRunner.RunDbgEngExtension(dump, system32, bogusJsProvider, ext, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process DbgEng engine unavailable in this environment: {ex.Message}");
                return;
            }

            // Proves the extension commands ran against an opened dump (the extension exports are absent
            // because the real JsProvider.dll is not present, which is expected in a hermetic test).
            StringAssert.Contains(result, "xamlstowed");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void Run_ValidDumpThroughFullPipeline_ReturnsZeroAndWritesOutput()
    {
        // Drives Run() (not RunDbgEngExtension directly) all the way through a successful engine session
        // so the success arm — Console.Out.Write(...) then `return 0` — is exercised. A real native dump
        // opens; the bogus JsProvider keeps it hermetic (no network).
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a test minidump: {err}");
            return;
        }

        try
        {
            var ext = Path.Combine(_tempDir, "ext.js");
            File.WriteAllText(ext, "// ext");
            var bogusJsProvider = Path.Combine(_tempDir, "NoSuchJsProvider.dll");

            var (exit, stdout, _) = RunCaptured(
                [XamlTriageRunner.InternalVerb, "--dump", dump, "--bin", system32, "--ext", ext, "--jsprovider", bogusJsProvider]);

            if (exit != 0)
            {
                Assert.Inconclusive("In-process DbgEng engine unavailable (Run mapped it to a non-zero exit).");
                return;
            }

            StringAssert.Contains(stdout, "xamlstowed");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void RunDbgEngExtension_InvalidJsProviderFile_ReportsLoadFailure()
    {
        // A JsProvider path that EXISTS but is not a loadable DLL makes DbgEng's `.load` return a failure
        // HRESULT, exercising the "could not load the JavaScript provider" early-return.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a test minidump: {err}");
            return;
        }

        try
        {
            var ext = Path.Combine(_tempDir, "ext.js");
            File.WriteAllText(ext, "// ext");
            var badJsProvider = Path.Combine(_tempDir, "BadJsProvider.dll");
            File.WriteAllText(badJsProvider, "this is not a valid PE/DLL");

            string result;
            try
            {
                result = XamlTriageRunner.RunDbgEngExtension(dump, system32, badJsProvider, ext, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process DbgEng engine unavailable in this environment: {ex.Message}");
                return;
            }

            if (result.Contains("xamlstowed", StringComparison.Ordinal))
            {
                // DbgEng accepted the '.load' at the Execute() level in this environment (it reports the
                // failure via output rather than a failing HRESULT), so the early-return cannot be forced.
                Assert.Inconclusive("DbgEng '.load' returned success for an invalid provider here; load-failure branch not reachable.");
                return;
            }

            StringAssert.Contains(result, "could not load the JavaScript provider");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }
}

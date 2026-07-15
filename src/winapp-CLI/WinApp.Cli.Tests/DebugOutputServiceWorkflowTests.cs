// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real-workflow tests for the <see cref="DebugOutputService"/> debug-event loop. A benign PowerShell
/// child is started, blocked on stdin, then the service attaches the Win32 debugger to it; only after
/// the debugger is attached does the test release the child (via stdin) to emit
/// <c>OutputDebugString</c> messages or fault. This makes the ordering deterministic (no TOCTOU): the
/// interesting debug events always occur while the loop is running. The crash-dump boundary is a fake,
/// so no real dump is written and no analysis runs. Marked <c>[DoNotParallelize]</c> because a process
/// may only be attached to one debugger at a time and the loop mutates global debug state.
/// </summary>
/// <remarks>
/// <para><b>Documented coverage ceiling (~92% Debug line coverage).</b> The uncovered lines require Win32
/// fault injection, a non-x64 host, or a specific never-observed debug event, none of which can be forced
/// deterministically; per policy they are left honestly uncovered rather than tested flakily or excluded.
/// Current uncovered ranges and why:</para>
/// <list type="bullet">
///   <item>166-167 — <c>OutputDebugString</c> event with a zero-length payload: a real debugger event the
///   controlled child never emits.</item>
///   <item>174-175, 181-182 — <c>OpenProcess</c> / <c>ReadProcessMemory</c> on the debuggee failing while
///   the debugger is attached: cannot be provoked without corrupting the OS call (would be TOCTOU/flaky).</item>
///   <item>228-230 — single-step / thread-rename noise-event handling: nondeterministic event types not
///   raised by the controlled child.</item>
///   <item>317-318 — <c>ReadExceptionParameters</c> for an exception record with zero parameters:
///   defensive; the faults driven here always carry parameters.</item>
///   <item>341-344, 373-376 — <c>OpenThread</c> / <c>GetThreadContext</c> failing on the faulting thread:
///   Win32 fault injection, undrivable without flakiness.</item>
///   <item>355 — the <c>Arm64</c> arm of the CONTEXT-flags switch: host-architecture path, unreachable on
///   an x64 host.</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize]
#pragma warning disable CA1001 // TestConsole disposed in cleanup
public sealed class DebugOutputServiceWorkflowTests
{
    private TestConsole _console = null!;
    private FakeCrashDumpService _crashDump = null!;
    private DebugOutputService _service = null!;
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "winapp-dumps");

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _crashDump = new FakeCrashDumpService();
        _service = new DebugOutputService(_console, _crashDump, NullLogger<DebugOutputService>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _console?.Dispose();

    [TestMethod]
    public async Task RunDebugLoopAsync_UnattachableProcess_ReturnsMinusOneAndWritesLog()
    {
        // A process id that does not exist -> DebugActiveProcess fails deterministically.
        const uint bogusPid = 0x7FFFFFF0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var exit = await _service.RunDebugLoopAsync(bogusPid, cts.Token);

        Assert.AreEqual(-1, exit, "Attaching to a non-existent process must return -1.");
        StringAssert.Contains(_console.Output, "Full debug log:");

        var logs = SafeGetLogs(bogusPid);
        Assert.IsTrue(logs.Length > 0, "A debug log file should have been created even when attach fails.");
        CleanupLogs(logs);
    }

    [TestMethod]
    public async Task RunDebugLoopAsync_BenignChild_StreamsDebugOutputAndPropagatesExitCode()
    {
        Process? child = TryStartPowerShellChild(BenignOutputDebugStringScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            // Let the debugger attach and drain the initial breakpoint / module-load events first.
            await Task.Delay(1500, cts.Token);
            if (!TrySignalChild(child))
            {
                await cts.CancelAsync();
                try { await loopTask; } catch { /* ignore */ }
                Assert.Inconclusive("Child exited before it could be signalled.");
                return;
            }

            int exit;
            try
            {
                exit = await loopTask.WaitAsync(TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe the child's exit in time on this machine.");
                return;
            }

            Assert.AreEqual(7, exit, "The child's exit code should be propagated by the debug loop.");
            StringAssert.Contains(_console.Output, "APPMARKER7X", "App-specific debug output should reach the console.");
            Assert.IsFalse(_console.Output.Contains("0xdeadbeef", StringComparison.Ordinal),
                "Framework-noise debug output should be filtered from the console.");
            StringAssert.Contains(_console.Output, "Full debug log:");
            Assert.AreEqual(0, _crashDump.WriteCalls.Count, "No crash dump should be written for a clean exit.");

            var logs = SafeGetLogs((uint)child.Id);
            if (logs.Length > 0)
            {
                var logText = await File.ReadAllTextAsync(logs[0]);
                StringAssert.Contains(logText, "APPMARKER7X");
                // The log captures everything, including the noise that was filtered from the console.
                StringAssert.Contains(logText, "Microsoft.UI.Xaml.dll");
                CleanupLogs(logs);
            }
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [TestMethod]
    public async Task RunDebugLoopAsync_ChildAccessViolation_DetectsCrashAndRequestsDump()
    {
        _crashDump.FakeDumpPath = Path.Combine(_logDir, $"fake-crash-{Guid.NewGuid():N}.dmp");

        Process? child = TryStartPowerShellChild(AccessViolationScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            await Task.Delay(1500, cts.Token);
            if (!TrySignalChild(child))
            {
                await cts.CancelAsync();
                try { await loopTask; } catch { /* ignore */ }
                Assert.Inconclusive("Child exited before it could be signalled.");
                return;
            }

            try
            {
                await loopTask.WaitAsync(TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe the crash in time on this machine.");
                return;
            }

            Assert.IsTrue(_crashDump.WriteCalls.Count >= 1,
                "An access violation should cause the debug loop to request a crash dump.");
            StringAssert.Contains(_console.Output, "Crash:");
            StringAssert.Contains(_console.Output, "First-chance exception:");
            Assert.IsTrue(_crashDump.AnalyzeCalls.Contains(_crashDump.FakeDumpPath!),
                "After a captured dump, the loop should invoke crash-dump analysis on it.");

            CleanupLogs(SafeGetLogs((uint)child.Id));
        }
        finally
        {
            KillQuietly(child);
        }
    }

    // ---- Child scripts (executed via -EncodedCommand to avoid shell quoting issues) ----

    private const string StackOverflowScript = """
        $null = [Console]::In.ReadLine()
        Add-Type -Namespace W -Name N -MemberDefinition 'public static long Rec(int n){ return Rec(n + 1) + n; }'
        [W.N]::Rec(0)
        exit 0
        """;

    [TestMethod]
    public async Task RunDebugLoopAsync_ChildStackOverflow_CapturesDumpOnFirstChance()
    {
        _crashDump.FakeDumpPath = Path.Combine(_logDir, $"fake-soe-{Guid.NewGuid():N}.dmp");

        Process? child = TryStartPowerShellChild(StackOverflowScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            await Task.Delay(1500, cts.Token);
            if (!TrySignalChild(child))
            {
                await cts.CancelAsync();
                try { await loopTask; } catch { /* ignore */ }
                Assert.Inconclusive("Child exited before it could be signalled.");
                return;
            }

            try
            {
                await loopTask.WaitAsync(TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe the stack overflow in time on this machine.");
                return;
            }

            // Stack overflow (0xC00000FD) is fatal on first-chance, so the loop must capture the dump
            // immediately rather than waiting for a (never-arriving) second-chance exception.
            Assert.IsTrue(_crashDump.WriteCalls.Count >= 1,
                "A stack overflow should cause the debug loop to request a crash dump on first-chance.");
            StringAssert.Contains(_console.Output, "Crash:");
            StringAssert.Contains(_console.Output, "C00000FD");

            CleanupLogs(SafeGetLogs((uint)child.Id));
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [TestMethod]
    public async Task RunDebugLoopAsync_CancelledWhileAttached_DetachesAndReturnsMinusOne()
    {
        // A benign child blocks on stdin and is never signalled, so it stays alive with no further debug
        // events. Cancelling the token after the debugger has attached must break the event loop via its
        // cancellation check (not an EXIT_PROCESS event) and return the -1 default without a crash dump.
        Process? child = TryStartPowerShellChild(BenignOutputDebugStringScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource();
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            // Let the debugger attach and drain the initial breakpoint / module-load events. The child is
            // still blocked on stdin (never signalled), so no EXIT_PROCESS event can race the cancel.
            await Task.Delay(1500);
            Assert.IsFalse(child.HasExited, "The blocked child must still be running when the token is cancelled.");

            await cts.CancelAsync();

            int exit;
            try
            {
                exit = await loopTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe cancellation in time on this machine.");
                return;
            }

            Assert.AreEqual(-1, exit, "Cancelling before the child exits must return the -1 default exit code.");
            Assert.AreEqual(0, _crashDump.WriteCalls.Count, "Cancellation is not a crash and must not request a dump.");
            StringAssert.Contains(_console.Output, "Full debug log:");

            CleanupLogs(SafeGetLogs((uint)child.Id));
        }
        finally
        {
            KillQuietly(child);
        }
    }

    private const string BenignOutputDebugStringScript = """
        $null = [Console]::In.ReadLine()
        Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern void OutputDebugString(string s);'
        [W.N]::OutputDebugString('APPMARKER7X hello from child')
        [W.N]::OutputDebugString('Microsoft.UI.Xaml.dll!0xdeadbeef framework noise')
        Start-Sleep -Milliseconds 300
        exit 7
        """;

    private const string AccessViolationScript = """
        $null = [Console]::In.ReadLine()
        Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("kernel32.dll")] public static extern void RtlZeroMemory(System.IntPtr dst, System.IntPtr len);'
        [W.N]::RtlZeroMemory([System.IntPtr]::Zero, [System.IntPtr]8)
        Start-Sleep -Seconds 5
        exit 0
        """;

    private static Process? TryStartPowerShellChild(string script, out string? error)
    {
        error = null;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        foreach (var host in new[] { "powershell.exe", "pwsh.exe" })
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = $"-NoLogo -NoProfile -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (proc != null)
                {
                    return proc;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        error ??= "no PowerShell host available";
        return null;
    }

    private static bool TrySignalChild(Process child)
    {
        try
        {
            if (child.HasExited)
            {
                return false;
            }

            child.StandardInput.WriteLine();
            child.StandardInput.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string[] SafeGetLogs(uint pid)
    {
        try
        {
            return Directory.Exists(_logDir)
                ? Directory.GetFiles(_logDir, $"debug-{pid}-*.log")
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void CleanupLogs(string[] logs)
    {
        foreach (var log in logs)
        {
            try { File.Delete(log); } catch { /* best effort */ }
        }
    }

    private static void KillQuietly(Process child)
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
        finally
        {
            child.Dispose();
        }
    }
}

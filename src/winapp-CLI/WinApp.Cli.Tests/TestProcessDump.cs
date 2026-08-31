// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Test helper that produces a real, valid minidump of a benign, short-lived native child process
/// via the product's own <see cref="CrashDumpService.WriteMiniDump"/>. This gives engine/analysis
/// tests a genuine dump file to open without relying on any external debugger or network download.
/// </summary>
internal static class TestProcessDump
{
    /// <summary>
    /// Spawns a benign native child (<c>ping</c>) and writes a normal minidump of it. Returns the dump
    /// path, or <c>null</c> (with a reason in <paramref name="error"/>) when dumping is not possible.
    /// The caller owns deleting the returned file.
    /// </summary>
    public static string? TryCreateNativeDump(out string? error)
    {
        error = null;
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = "ping.exe",
                Arguments = "-n 60 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (child == null)
            {
                error = "could not start the child process for dump capture";
                return null;
            }

            // Let the child fully initialize so its memory is dumpable.
            Thread.Sleep(300);

            var service = new CrashDumpService(
                new TestConsole(),
                NullLogger<CrashDumpService>.Instance,
                new FakeXamlTriageService());

            var dumpPath = service.WriteMiniDump(
                (uint)child.Id, savedContext: null, savedThreadId: 0,
                savedExceptionCode: 0, savedExceptionAddress: 0);
            if (dumpPath == null)
            {
                error = "WriteMiniDump returned null";
                return null;
            }

            return dumpPath;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            try
            {
                if (child != null && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }

                child?.Dispose();
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// Spawns a benign managed child (PowerShell) and writes a full-memory minidump of it, so ClrMD
    /// can enumerate the CLR. Returns the dump path or <c>null</c> (with a reason) when not possible.
    /// The caller owns deleting the returned file.
    /// </summary>
    /// <remarks>
    /// The child proves it is ready and the dump is taken only once that proof arrives, because a host
    /// that has been <em>started</em> is not yet a host with a walkable CLR in it. A fixed 1.5s sleep
    /// stood in for that proof and did not hold on a loaded agent: CI captured dumps whose only stack
    /// was <c>hostfxr</c>/<c>hostpolicy</c> still loading coreclr, so ClrMD had nothing to enumerate,
    /// the analysis reported "No CLR runtime found in dump (native-only crash)", and the managed
    /// assertions failed intermittently. See <see cref="ManagedChildCandidates"/> for what the child
    /// does before signalling and why merely reaching managed code is not enough on its own.
    /// </remarks>
    public static string? TryCreateManagedDump(out string? error)
    {
        error = null;
        foreach (var (fileName, args) in ManagedChildCandidates())
        {
            Process? child = null;
            try
            {
                child = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (child == null)
                {
                    continue;
                }

                // Drain stderr so a chatty host can never fill the pipe and wedge the child.
                var draining = child;
                _ = Task.Run(() => { try { draining.StandardError.ReadToEnd(); } catch { /* ignore */ } });

                if (!WaitForToken(child, ManagedReadyToken, TimeSpan.FromSeconds(30)))
                {
                    error = $"managed child did not emit readiness token '{ManagedReadyToken}' (host {fileName})";
                    KillQuietly(child);
                    child = null;
                    continue;
                }

                var dumpPath = DumpRunningChild(child);
                if (dumpPath != null)
                {
                    return dumpPath;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                if (child != null)
                {
                    KillQuietly(child);
                }
            }
        }

        error ??= "no managed host (pwsh/powershell) could be started for dump capture";
        return null;
    }

    /// <summary>Readiness token the benign managed child emits once its CLR is up and its heap is warm.</summary>
    private const string ManagedReadyToken = "MANAGED_READY";

    private static IEnumerable<(string FileName, string Args)> ManagedChildCandidates()
    {
        // The child allocates, forces a blocking collection, and only then reports readiness, so the
        // token means "the runtime is up AND its heap is populated and settled" rather than merely "a
        // process exists". Both halves matter and neither is decoration:
        //
        //  - Signalling from managed code is what rules out dumping a host still in NATIVE startup.
        //    That was the CI failure: dumps whose only stack was hostfxr/hostpolicy loading coreclr, so
        //    ClrMD found no runtime and the analysis reported a native-only crash.
        //  - Allocating and collecting first is what rules out the opposite mistake. A token emitted by
        //    the first line of managed code proves too little -- ClrMD needs walkable GC structures, and
        //    dumping that early trades the old race for a new one (measured: signalling immediately made
        //    this test fail or go inconclusive on a machine where the previous code passed).
        //
        // [Console]::Out with an explicit Flush, not Write-Object: the token must reach the reader as
        // soon as it is written, which only an unbuffered write to the real stdout handle guarantees
        // once the stream is redirected. Single quotes throughout so the script survives being embedded
        // in the double-quoted -Command argument.
        const string script =
            "$sink = 1..2000 | ForEach-Object { [pscustomobject]@{ I = $_; S = 'warm' + $_ } }; " +
            "[GC]::Collect(); " +
            "[GC]::WaitForPendingFinalizers(); " +
            $"[Console]::Out.WriteLine('{ManagedReadyToken}'); " +
            "[Console]::Out.Flush(); " +
            "Start-Sleep -Seconds 120";

        yield return ("pwsh.exe", $"-NoLogo -NoProfile -Command \"{script}\"");
        yield return ("powershell.exe", $"-NoLogo -NoProfile -Command \"{script}\"");
    }

    /// <summary>
    /// Managed dump whose faulting thread is blocked <em>inside an exception filter</em>, so the
    /// exception is still in flight and ClrMD reports it via <c>ClrThread.CurrentException</c>.
    /// </summary>
    public static string? TryCreateManagedDumpWithInFlightException(out string? error)
    {
        const string members = """
            public static bool Filter() {
                System.Console.Out.WriteLine("INFLIGHT_READY");
                System.Console.Out.Flush();
                System.Threading.Thread.Sleep(120000);
                return true;
            }
            public static void Go() {
                try { throw new System.InvalidOperationException("INFLIGHT_BOOM"); }
                catch when (Filter()) { }
            }
            """;
        return RunManagedScriptAndDump(members, "[W.N]::Go()", "INFLIGHT_READY", out error);
    }

    /// <summary>
    /// Managed dump containing a fully-unwound exception (with an inner exception and a deep recorded
    /// stack trace) kept alive on the heap, so ClrMD's heap-scan fallback finds it and formats it.
    /// </summary>
    public static string? TryCreateManagedDumpWithHeapException(out string? error)
    {
        const string members = """
            private static System.Exception _kept;
            public static void ThrowDeep(int n) {
                if (n <= 0) throw new System.InvalidOperationException("HEAP_BOOM_OUTER", new System.FormatException("HEAP_BOOM_INNER"));
                ThrowDeep(n - 1);
            }
            public static void Go() {
                try { ThrowDeep(20); } catch (System.Exception e) { _kept = e; }
                System.Console.Out.WriteLine("HEAPEXC_READY");
                System.Console.Out.Flush();
                System.Threading.Thread.Sleep(120000);
            }
            """;
        return RunManagedScriptAndDump(members, "[W.N]::Go()", "HEAPEXC_READY", out error);
    }

    /// <summary>
    /// Managed dump whose faulting thread is parked at the bottom of a deep (but bounded, non-faulting)
    /// recursion, so ClrMD sees a thread with hundreds of managed frames — the shape the analyzer treats
    /// as a stack overflow.
    /// </summary>
    public static string? TryCreateManagedDumpWithDeepStack(out string? error)
    {
        error = null;
        var exePath = Path.Combine(Path.GetTempPath(), "winapp_deepstack_" + Guid.NewGuid().ToString("N") + ".exe");
        const string src = """
            using System;
            using System.Threading;
            public static class P {
                public static long Deep(int n) {
                    if (n <= 0) { Console.Out.WriteLine("DEEP_READY"); Console.Out.Flush(); Thread.Sleep(120000); return 0; }
                    return Deep(n - 1) + n; // not a tail call: the add after the call forces the frame to be kept
                }
                public static void Main() { Console.Out.WriteLine(Deep(600)); }
            }
            """;

        // A bespoke minimal process is required (rather than scripting a PowerShell host): ClrMD's
        // stack-overflow heuristic only runs when NO managed exception exists anywhere in the dump, and
        // a real PowerShell host always leaves caught exceptions (with recorded stack traces) on the
        // heap, which short-circuits the heuristic. The exe is compiled on disk so ClrMD can resolve
        // its managed frames (methods emitted into an in-memory dynamic assembly report a null method).
        if (!TryCompileConsoleExe(src, exePath, out error))
        {
            return null;
        }

        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (child == null)
            {
                error = "could not start the compiled deep-stack child";
                return null;
            }

            var draining = child;
            _ = Task.Run(() => { try { draining.StandardError.ReadToEnd(); } catch { /* ignore */ } });

            if (!WaitForToken(child, "DEEP_READY", TimeSpan.FromSeconds(30)))
            {
                error = "deep-stack child did not emit readiness token";
                return null;
            }

            return DumpRunningChild(child);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (child != null)
            {
                KillQuietly(child);
            }

            try { File.Delete(exePath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Waits for <paramref name="process"/> to exit within <paramref name="timeoutMs"/> while draining
    /// BOTH stdout and stderr concurrently. Reading the two pipes concurrently (rather than one
    /// <see cref="System.IO.StreamReader.ReadToEnd"/> after the other) prevents the classic deadlock
    /// where the child blocks writing to the pipe we are not yet reading while we block waiting for EOF
    /// on the other. Kills the process tree on timeout. Returns true iff the process exited in time.
    /// </summary>
    private static bool WaitForExitDrainingPipes(Process process, int timeoutMs, out string stdout, out string stderr)
    {
        // Start both reads BEFORE waiting so neither pipe can back-pressure (and stall) the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            // Killing closes the pipes, so the drains complete; bound the wait defensively.
            try { Task.WaitAll([stdoutTask, stderrTask], 5000); } catch { /* best effort */ }
            stdout = SafeResult(stdoutTask);
            stderr = SafeResult(stderrTask);
            return false;
        }

        stdout = stdoutTask.GetAwaiter().GetResult();
        stderr = stderrTask.GetAwaiter().GetResult();
        return true;

        static string SafeResult(Task<string> t)
        {
            try { return t.IsCompletedSuccessfully ? t.Result : string.Empty; }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// Compiles a standalone .NET console EXE from C# source using the .NET Framework compiler that
    /// <c>Add-Type -OutputType ConsoleApplication</c> drives (available via <c>powershell.exe</c>).
    /// </summary>
    private static bool TryCompileConsoleExe(string csharpSource, string outputExePath, out string? error)
    {
        error = null;
        var script = $"""
            $ErrorActionPreference = 'Stop'
            $src = @'
            {csharpSource}
            '@
            Add-Type -TypeDefinition $src -OutputAssembly '{outputExePath}' -OutputType ConsoleApplication
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        // -OutputType ConsoleApplication is a Windows PowerShell (.NET Framework) capability.
        foreach (var host in new[] { "powershell.exe", "pwsh.exe" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = $"-NoLogo -NoProfile -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p == null)
                {
                    continue;
                }

                if (!WaitForExitDrainingPipes(p, 60000, out _, out var stderr))
                {
                    error = $"compilation via {host} timed out";
                    continue;
                }

                if (File.Exists(outputExePath))
                {
                    return true;
                }

                error = $"compilation via {host} produced no exe: {stderr}";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        error ??= "no PowerShell host could compile the deep-stack exe";
        return false;
    }

    private static string? RunManagedScriptAndDump(string csharpMembers, string entry, string readyToken, out string? error)
    {
        error = null;
        var script = $"""
            $ErrorActionPreference = 'Stop'
            $src = @'
            {csharpMembers}
            '@
            Add-Type -Namespace W -Name N -MemberDefinition $src
            {entry}
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        foreach (var host in new[] { "powershell.exe", "pwsh.exe" })
        {
            Process? child = null;
            try
            {
                child = Process.Start(new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = $"-NoLogo -NoProfile -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (child == null)
                {
                    continue;
                }

                // Drain stderr so a chatty compile error can never fill the pipe and deadlock us.
                var draining = child;
                _ = Task.Run(() => { try { draining.StandardError.ReadToEnd(); } catch { /* ignore */ } });

                if (!WaitForToken(child, readyToken, TimeSpan.FromSeconds(30)))
                {
                    error = $"child did not emit readiness token '{readyToken}' (host {host})";
                    KillQuietly(child);
                    child = null;
                    continue;
                }

                var dumpPath = DumpRunningChild(child);
                if (dumpPath != null)
                {
                    return dumpPath;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                if (child != null)
                {
                    KillQuietly(child);
                }
            }
        }

        error ??= "no managed host (pwsh/powershell) could be started for scripted dump capture";
        return null;
    }

    private static bool WaitForToken(Process child, string token, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var reader = child.StandardOutput;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            // Issue exactly one read at a time (overlapping ReadLineAsync on one reader throws) and
            // wait for it to complete within the remaining budget.
            var lineTask = reader.ReadLineAsync();
            if (!lineTask.Wait(remaining))
            {
                return false;
            }

            var line = lineTask.Result;
            if (line == null)
            {
                return false; // stream closed (child exited)
            }

            if (line.Contains(token, StringComparison.Ordinal))
            {
                // Give the thread a beat to settle into its blocking Sleep before dumping.
                Thread.Sleep(250);
                return true;
            }
        }
    }

    private static string? DumpRunningChild(Process child)
    {
        var service = new CrashDumpService(
            new TestConsole(),
            NullLogger<CrashDumpService>.Instance,
            new FakeXamlTriageService());

        return service.WriteMiniDump(
            (uint)child.Id, savedContext: null, savedThreadId: 0,
            savedExceptionCode: 0, savedExceptionAddress: 0);
    }

    private static void KillQuietly(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            child.Dispose();
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Like <see cref="TryCreateManagedDumpWithDeepStack"/> but compiles the deep-recursion child with the
    /// .NET SDK (<c>dotnet build</c>) so a <em>portable</em> PDB is emitted next to the managed module. This
    /// is the only way to exercise <c>PdbSourceResolver</c>'s on-disk portable-PDB path (sequence-point walk
    /// and successful reader load): the <c>Add-Type</c> build emits a Windows PDB, which the portable-PDB
    /// reader rejects. The build output (containing <c>deepstack.dll</c> + <c>deepstack.pdb</c>) is returned
    /// via <paramref name="moduleDir"/> so it can be passed as a symbol search path; <paramref name="tempRoot"/>
    /// must be deleted by the caller <em>after</em> analysis (ClrMD reads the DLL/PDB during the analysis pass).
    /// Returns null with <paramref name="error"/> when the SDK build is unavailable (test should be Inconclusive).
    /// </summary>
    public static string? TryCreateDeepStackDumpWithPortablePdb(out string? error, out string? tempRoot, out string? moduleDir)
    {
        error = null;
        moduleDir = null;
        var root = Path.Combine(Path.GetTempPath(), "winapp_deeppdb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        tempRoot = root;

        const string program = """
            using System;
            using System.Threading;
            public static class P {
                public static long Deep(int n) {
                    if (n <= 0) { Console.Out.WriteLine("DEEP_READY"); Console.Out.Flush(); Thread.Sleep(120000); return 0; }
                    return Deep(n - 1) + n; // not a tail call: the trailing add forces the frame to be kept
                }
                public static void Main() { Console.Out.WriteLine(Deep(600)); }
            }
            """;
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <DebugType>portable</DebugType>
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <AssemblyName>deepstack</AssemblyName>
                <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
              </PropertyGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(root, "Program.cs"), program);
        File.WriteAllText(Path.Combine(root, "deepstack.csproj"), csproj);

        if (!TryRunDotnetBuild(root, out error))
        {
            return null;
        }

        var outDir = Path.Combine(root, "bin", "Debug", "net10.0");
        var exePath = Path.Combine(outDir, "deepstack.exe");
        if (!File.Exists(exePath))
        {
            error = $"expected build output missing: {exePath}";
            return null;
        }

        moduleDir = outDir;

        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (child == null)
            {
                error = "could not start the compiled deep-stack child";
                return null;
            }

            var draining = child;
            _ = Task.Run(() => { try { draining.StandardError.ReadToEnd(); } catch { /* ignore */ } });

            if (!WaitForToken(child, "DEEP_READY", TimeSpan.FromSeconds(30)))
            {
                error = "deep-stack child did not emit readiness token";
                return null;
            }

            return DumpRunningChild(child);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (child != null)
            {
                KillQuietly(child);
            }

            // NOTE: 'root' is intentionally not deleted here — the caller needs deepstack.dll/.pdb on disk
            // for the source-resolution pass, and deletes tempRoot once analysis has completed.
        }
    }

    private static bool TryRunDotnetBuild(string projectDir, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = projectDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Debug");
            psi.ArgumentList.Add("-p:UseSharedCompilation=false");
            psi.ArgumentList.Add("--nologo");

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                error = "failed to start 'dotnet build'";
                return false;
            }

            if (!WaitForExitDrainingPipes(proc, 120000, out var stdout, out var stderr))
            {
                error = "'dotnet build' timed out";
                return false;
            }

            if (proc.ExitCode != 0)
            {
                error = "'dotnet build' failed: " + stdout + stderr;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

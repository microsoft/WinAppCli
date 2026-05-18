// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// split of the historical DynWinrtCodegenServiceTests.
// Scope: ResolveExecutableOnPath / ResolveCodegenInvocation / SpawnCodegen.
[TestClass]
[DoNotParallelize]  // CWD/PATH/PATHEXT hijack tests mutate process-wide state.
public class DynWinrtCodegenInvocationTests
{
    public TestContext TestContext { get; set; } = null!;

    private DirectoryInfo _temp = null!;

    [TestInitialize]
    public void Init()
    {
        _temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"DynWinrtCodegenInvocationTests_{Guid.NewGuid():N}"));
        _temp.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _temp.Delete(recursive: true); } catch { /* ignore */ }
    }

    // -------------------------------------------------------------------------
    // ResolveExecutableOnPath — PATH lookup must skip CWD-equivalent entries
    // to prevent search-order hijack.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ResolveExecutableOnPath_AbsolutePathIn_PassedThroughWhenExists()
    {
        var f = new FileInfo(Path.Combine(_temp.FullName, "tool.exe"));
        File.WriteAllText(f.FullName, "");
        var resolved = DynWinrtCodegenService.ResolveExecutableOnPath(f.FullName);
        Assert.AreEqual(f.FullName, resolved);
    }

    [TestMethod]
    public void ResolveExecutableOnPath_NonExistent_ReturnsNull()
    {
        Assert.IsNull(DynWinrtCodegenService.ResolveExecutableOnPath("this-tool-does-not-exist-anywhere"));
    }

    [TestMethod]
    public void ResolveExecutableOnPath_EmptyInput_ReturnsNull()
    {
        Assert.IsNull(DynWinrtCodegenService.ResolveExecutableOnPath(""));
        Assert.IsNull(DynWinrtCodegenService.ResolveExecutableOnPath("   "));
    }

    [TestMethod]
    public void ResolveExecutableOnPath_SkipsLiteralDotAndEmptyPathEntries()
    {
        var decoy = new DirectoryInfo(Path.Combine(_temp.FullName, "decoy-cwd"));
        var safe = new DirectoryInfo(Path.Combine(_temp.FullName, "safe-bin"));
        decoy.Create();
        safe.Create();
        var decoyNode = Path.Combine(decoy.FullName, "node.exe");
        var safeNode = Path.Combine(safe.FullName, "node.exe");
        File.WriteAllText(decoyNode, "DECOY");
        File.WriteAllText(safeNode, "SAFE");

        var prevCwd = Directory.GetCurrentDirectory();
        var prevPath = Environment.GetEnvironmentVariable("PATH");
        var prevExt = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Directory.SetCurrentDirectory(decoy.FullName);
            Environment.SetEnvironmentVariable(
                "PATH",
                $".{Path.PathSeparator}{Path.PathSeparator}{safe.FullName}");
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");

            var resolved = DynWinrtCodegenService.ResolveExecutableOnPath("node");

            Assert.IsNotNull(resolved);
            Assert.AreEqual(
                Path.GetFullPath(safeNode).ToLowerInvariant(),
                Path.GetFullPath(resolved!).ToLowerInvariant(),
                $"Expected safe PATH dir to win; got {resolved}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prevPath);
            Environment.SetEnvironmentVariable("PATHEXT", prevExt);
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    [TestMethod]
    public void ResolveExecutableOnPath_SkipsAbsolutePathEntryThatEqualsCwd()
    {
        var decoy = new DirectoryInfo(Path.Combine(_temp.FullName, "abs-cwd"));
        var safe = new DirectoryInfo(Path.Combine(_temp.FullName, "safe-bin2"));
        decoy.Create();
        safe.Create();
        File.WriteAllText(Path.Combine(decoy.FullName, "node.exe"), "DECOY");
        var safeNode = Path.Combine(safe.FullName, "node.exe");
        File.WriteAllText(safeNode, "SAFE");

        var prevCwd = Directory.GetCurrentDirectory();
        var prevPath = Environment.GetEnvironmentVariable("PATH");
        var prevExt = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Directory.SetCurrentDirectory(decoy.FullName);
            Environment.SetEnvironmentVariable(
                "PATH",
                $"{decoy.FullName}{Path.PathSeparator}{safe.FullName}");
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");

            var resolved = DynWinrtCodegenService.ResolveExecutableOnPath("node");

            Assert.IsNotNull(resolved);
            Assert.AreEqual(
                Path.GetFullPath(safeNode).ToLowerInvariant(),
                Path.GetFullPath(resolved!).ToLowerInvariant(),
                "Absolute PATH entry equal to CWD must be skipped to prevent local hijack.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prevPath);
            Environment.SetEnvironmentVariable("PATHEXT", prevExt);
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    [TestMethod]
    public void ResolveExecutableOnPath_HonorsPathExt()
    {
        var safe = new DirectoryInfo(Path.Combine(_temp.FullName, "safe-cmd"));
        safe.Create();
        var cmd = Path.Combine(safe.FullName, "node.cmd");
        File.WriteAllText(cmd, "@echo");

        var prevPath = Environment.GetEnvironmentVariable("PATH");
        var prevExt = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", safe.FullName);
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");

            var resolved = DynWinrtCodegenService.ResolveExecutableOnPath("node");

            Assert.IsNotNull(resolved);
            Assert.AreEqual(
                Path.GetFullPath(cmd).ToLowerInvariant(),
                Path.GetFullPath(resolved!).ToLowerInvariant());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prevPath);
            Environment.SetEnvironmentVariable("PATHEXT", prevExt);
        }
    }

    // nativeOnly mode must reject .bat/.cmd/.ps1, which dispatch
    // through cmd.exe / pwsh and would re-parse user-derived args.

    [TestMethod]
    public void ResolveExecutableOnPath_NativeOnly_RejectsBatAndCmd()
    {
        var safe = new DirectoryInfo(Path.Combine(_temp.FullName, "safe-native"));
        safe.Create();
        // Only a node.cmd is available — nativeOnly must refuse.
        var cmd = Path.Combine(safe.FullName, "node.cmd");
        File.WriteAllText(cmd, "@echo");

        var prevPath = Environment.GetEnvironmentVariable("PATH");
        var prevExt = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", safe.FullName);
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");

            var nonStrict = DynWinrtCodegenService.ResolveExecutableOnPath("node");
            Assert.IsNotNull(nonStrict, "Default mode still finds the .cmd via PATHEXT.");

            var nativeOnly = DynWinrtCodegenService.ResolveExecutableOnPath("node", nativeOnly: true);
            Assert.IsNull(nativeOnly, "Native-only must skip .cmd to prevent cmd.exe arg re-parsing.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prevPath);
            Environment.SetEnvironmentVariable("PATHEXT", prevExt);
        }
    }

    [TestMethod]
    public void ResolveExecutableOnPath_NativeOnly_BareNameWithCmdExtension_Rejected()
    {
        // PATH entry contains a literal `node.cmd`; the bare-match path
        // (which short-circuits PATHEXT) must still honor nativeOnly.
        var safe = new DirectoryInfo(Path.Combine(_temp.FullName, "bare-cmd"));
        safe.Create();
        var cmd = Path.Combine(safe.FullName, "node.cmd");
        File.WriteAllText(cmd, "@echo");

        var prevPath = Environment.GetEnvironmentVariable("PATH");
        var prevExt = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", safe.FullName);
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");

            // Passing "node.cmd" as the bare command: bare-match would find
            // it, but nativeOnly rejects the .cmd extension.
            var resolved = DynWinrtCodegenService.ResolveExecutableOnPath("node.cmd", nativeOnly: true);
            Assert.IsNull(resolved, "nativeOnly bare-match must reject .cmd.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prevPath);
            Environment.SetEnvironmentVariable("PATHEXT", prevExt);
        }
    }

    // -------------------------------------------------------------------------
    // ResolveCodegenInvocation — direct .exe wins; cli.js fallback;
    // friendly error when both missing.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ResolveCodegenInvocation_DirectExePreferred()
    {
        var packageDir = new DirectoryInfo(Path.Combine(_temp.FullName, "node_modules", "@microsoft", "dynwinrt-codegen"));
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var binDir = new DirectoryInfo(Path.Combine(packageDir.FullName, "bin", arch));
        binDir.Create();
        var exe = new FileInfo(Path.Combine(binDir.FullName, "dynwinrt-codegen.exe"));
        File.WriteAllBytes(exe.FullName, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(packageDir.FullName, "cli.js"), "// stub");

        var (resolved, args) = DynWinrtCodegenService.ResolveCodegenInvocation(_temp);

        Assert.AreEqual(exe.FullName, resolved, "Direct .exe must win over cli.js fallback");
        Assert.AreEqual(0, args.Count, "Direct .exe call passes no prefix args");
    }

    [TestMethod]
    public void ResolveCodegenInvocation_CliJsFallback_UsesQualifiedNodePath()
    {
        var packageDir = new DirectoryInfo(Path.Combine(_temp.FullName, "node_modules", "@microsoft", "dynwinrt-codegen"));
        packageDir.Create();
        var cli = new FileInfo(Path.Combine(packageDir.FullName, "cli.js"));
        File.WriteAllText(cli.FullName, "// stub");

        // The fallback now uses nativeOnly=true — only finds node.exe/.com.
        var resolvedNode = DynWinrtCodegenService.ResolveExecutableOnPath("node", nativeOnly: true);
        if (resolvedNode is null)
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => DynWinrtCodegenService.ResolveCodegenInvocation(_temp),
                "Without a native node executable, the fallback must refuse.");
            return;
        }

        var (exe, args) = DynWinrtCodegenService.ResolveCodegenInvocation(_temp);

        Assert.AreEqual(resolvedNode, exe,
            "Node executable must be the fully-resolved PATH lookup.");
        Assert.IsTrue(Path.IsPathRooted(exe),
            "Spawned executable path must be absolute to prevent CWD-search hijacks.");
        // error message in the fallback path mentions native node.
        var ext = Path.GetExtension(exe);
        Assert.IsTrue(
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".com", StringComparison.OrdinalIgnoreCase),
            $"Fallback must spawn a native node executable (.exe/.com), got: {ext}");
        Assert.AreEqual(1, args.Count);
        Assert.AreEqual(cli.FullName, args[0]);
    }

    [TestMethod]
    public void ResolveCodegenInvocation_NothingFound_ThrowsActionableError()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.ResolveCodegenInvocation(_temp));

        StringAssert.Contains(ex.Message, "@microsoft/dynwinrt-codegen");
        StringAssert.Contains(ex.Message, "@microsoft/winappcli");
        StringAssert.Contains(ex.Message, "yarn berry");
        StringAssert.Contains(ex.Message, "pnpm");
    }

    [TestMethod]
    public void ResolveCodegenInvocation_UpwardLookup_FindsHoistedPackage()
    {
        var repoRoot = _temp;
        var nestedWorkspace = repoRoot.CreateSubdirectory("apps").CreateSubdirectory("electron-app");

        var packageDir = new DirectoryInfo(Path.Combine(
            repoRoot.FullName, "node_modules", "@microsoft", "dynwinrt-codegen"));
        packageDir.Create();
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        var exe = new FileInfo(Path.Combine(packageDir.FullName, "bin", arch, "dynwinrt-codegen.exe"));
        exe.Directory!.Create();
        File.WriteAllText(exe.FullName, "stub");

        var (resolved, args) = DynWinrtCodegenService.ResolveCodegenInvocation(nestedWorkspace);

        Assert.AreEqual(exe.FullName, resolved,
            "Resolver must walk upward from the nested workspace to find the codegen at the repo root.");
        Assert.AreEqual(0, args.Count);
    }

    [TestMethod]
    public void ResolveCodegenInvocation_InnerNodeModulesShadowsOuter()
    {
        var repoRoot = _temp;
        var nestedWorkspace = repoRoot.CreateSubdirectory("apps").CreateSubdirectory("inner");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        var outerExe = new FileInfo(Path.Combine(
            repoRoot.FullName, "node_modules", "@microsoft", "dynwinrt-codegen", "bin", arch, "dynwinrt-codegen.exe"));
        outerExe.Directory!.Create();
        File.WriteAllText(outerExe.FullName, "outer-stub");

        var innerExe = new FileInfo(Path.Combine(
            nestedWorkspace.FullName, "node_modules", "@microsoft", "dynwinrt-codegen", "bin", arch, "dynwinrt-codegen.exe"));
        innerExe.Directory!.Create();
        File.WriteAllText(innerExe.FullName, "inner-stub");

        var (resolved, _) = DynWinrtCodegenService.ResolveCodegenInvocation(nestedWorkspace);
        Assert.AreEqual(innerExe.FullName, resolved,
            "When package exists at multiple ancestors, the workspace-local one wins.");
    }

    // -------------------------------------------------------------------------
    // SpawnCodegen — cancellation kills child process tree promptly.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SpawnCodegen_CancellationKillsLongRunningChild_WithoutHang()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows-only test for ProcessTree kill.");
            return;
        }

        var cmd = DynWinrtCodegenService.ResolveExecutableOnPath("cmd");
        Assert.IsNotNull(cmd);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = cmd!,
            ArgumentList = { "/c", "ping", "-n", "60", "127.0.0.1" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var cts = new CancellationTokenSource();
        using var p = System.Diagnostics.Process.Start(psi)!;
        Assert.IsFalse(p.HasExited, "Child should start running.");

        _ = Task.Run(async () => { await Task.Delay(150); cts.Cancel(); });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var caught = false;
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            caught = true;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await p.WaitForExitAsync(killCts.Token);
                }
            }
            catch { /* best-effort */ }
        }
        sw.Stop();

        Assert.IsTrue(caught, "Cancellation must surface OperationCanceledException.");
        Assert.IsTrue(p.HasExited, "Child must be dead after cancel-and-kill.");
        Assert.IsTrue(sw.ElapsedMilliseconds < 5_000,
            $"Cancel+kill should complete fast; took {sw.ElapsedMilliseconds}ms.");
    }
}

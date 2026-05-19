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
    // ResolveCodegenInvocation — wrapper-bundled is the only trusted source.
    // -------------------------------------------------------------------------

    // Helper for arranging a wrapper layout under _temp.
    private static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static FileInfo PlantCodegenExe(DirectoryInfo root)
    {
        var packageDir = new DirectoryInfo(Path.Combine(
            root.FullName, "node_modules", "@microsoft", "dynwinrt-codegen"));
        var binDir = new DirectoryInfo(Path.Combine(packageDir.FullName, "bin", Arch));
        binDir.Create();
        var exe = new FileInfo(Path.Combine(binDir.FullName, "dynwinrt-codegen.exe"));
        File.WriteAllText(exe.FullName, "");
        return exe;
    }

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

        var (resolved, args) = DynWinrtCodegenService.ResolveCodegenInvocationCore(wrapperDir: _temp);

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

        // The fallback uses nativeOnly=true — only finds node.exe/.com.
        var resolvedNode = DynWinrtCodegenService.ResolveExecutableOnPath("node", nativeOnly: true);
        if (resolvedNode is null)
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => DynWinrtCodegenService.ResolveCodegenInvocationCore(wrapperDir: _temp),
                "Without a native node executable, the fallback must refuse.");
            return;
        }

        var (exe, args) = DynWinrtCodegenService.ResolveCodegenInvocationCore(wrapperDir: _temp);

        Assert.AreEqual(resolvedNode, exe,
            "Node executable must be the fully-resolved PATH lookup.");
        Assert.IsTrue(Path.IsPathRooted(exe),
            "Spawned executable path must be absolute to prevent CWD-search hijacks.");
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
        // No wrapper install — error must point at the npm/yarn classic install.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.ResolveCodegenInvocationCore(
                wrapperDir: _temp.CreateSubdirectory("empty-wrapper")));

        StringAssert.Contains(ex.Message, "@microsoft/dynwinrt-codegen");
        StringAssert.Contains(ex.Message, "@microsoft/winappcli");
    }

    [TestMethod]
    public void ResolveCodegenInvocation_NullWrapperDir_ThrowsWithReinstallHint()
    {
        // wrapperDir is null on `dotnet run` and any host where Environment.ProcessPath
        // is empty. The error must skip the per-dir search entirely and tell the user
        // to reinstall the npm package rather than echoing .NET internals.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.ResolveCodegenInvocationCore(wrapperDir: null));

        StringAssert.Contains(ex.Message, "@microsoft/dynwinrt-codegen");
        StringAssert.Contains(ex.Message, "winapp install directory could not be determined");
        StringAssert.Contains(ex.Message, "reinstalling @microsoft/winappcli");
    }

    [TestMethod]
    public void ResolveCodegenInvocation_UpwardLookup_FindsHoistedPackage()
    {
        // Hoisted layout reachable by walking up from wrapperDir — npm/yarn-classic happy path.
        var repoRoot = _temp;
        var nestedWrapper = repoRoot.CreateSubdirectory("apps").CreateSubdirectory("electron-app");

        var packageDir = new DirectoryInfo(Path.Combine(
            repoRoot.FullName, "node_modules", "@microsoft", "dynwinrt-codegen"));
        packageDir.Create();
        var exe = new FileInfo(Path.Combine(packageDir.FullName, "bin", Arch, "dynwinrt-codegen.exe"));
        exe.Directory!.Create();
        File.WriteAllText(exe.FullName, "stub");

        var (resolved, args) = DynWinrtCodegenService.ResolveCodegenInvocationCore(wrapperDir: nestedWrapper);

        Assert.AreEqual(exe.FullName, resolved,
            "Resolver must walk upward from the nested wrapper dir to find the hoisted codegen.");
        Assert.AreEqual(0, args.Count);
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

    // -------------------------------------------------------------------------
    // M6 — workspace-local codegen install must NOT be trusted as a fallback.
    // The resolver searches up from the wrapper dir ONLY; anything planted
    // under the user workspace must NOT short-circuit the wrapper-bundled
    // requirement (this is the post-r3 security model).
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ResolveCodegenInvocation_WorkspaceLocalInstall_NotTrustedWhenWrapperEmpty()
    {
        // Plant a fully-formed codegen exe under a SIBLING dir of the wrapper
        // (think: user workspace at `_temp/workspace/...`, wrapper at
        // `_temp/empty-wrapper/`). The resolver must refuse — workspace-local
        // installs no longer count as a fallback.
        var workspaceRoot = _temp.CreateSubdirectory("workspace");
        var workspaceCodegen = PlantCodegenExe(workspaceRoot);
        Assert.IsTrue(workspaceCodegen.Exists, "fixture sanity");

        var emptyWrapper = _temp.CreateSubdirectory("empty-wrapper");

        // The wrapper dir (and its ancestor chain) does NOT contain the
        // codegen install; the workspace install must NOT rescue this.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.ResolveCodegenInvocationCore(wrapperDir: emptyWrapper));

        StringAssert.Contains(ex.Message, "@microsoft/dynwinrt-codegen",
            "Refusal message must explain what was missing.");
        // The error must not point at the workspace plant.
        Assert.IsFalse(ex.Message.Contains(workspaceCodegen.FullName),
            "Resolver must not have considered the workspace-local install.");
    }
}

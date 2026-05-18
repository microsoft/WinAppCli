// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// split of the historical DynWinrtCodegenServiceTests.
// Scope: ResolveOutputDir / WipeOutputDirSafely / WriteManagedMarker —
// the "do not destroy user files" safety net.
[TestClass]
public class DynWinrtCodegenOutputSafetyTests
{
    public TestContext TestContext { get; set; } = null!;

    private DirectoryInfo _temp = null!;

    [TestInitialize]
    public void Init()
    {
        _temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"DynWinrtCodegenOutputSafetyTests_{Guid.NewGuid():N}"));
        _temp.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _temp.Delete(recursive: true); } catch { /* ignore */ }
    }

    // -------------------------------------------------------------------------
    // ResolveOutputDir — purely lexical, must not touch disk.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ResolveOutputDir_RelativePath_ResolvedAgainstWorkspace()
    {
        var dir = DynWinrtCodegenService.ResolveOutputDir(_temp, "bindings/winrt");
        StringAssert.StartsWith(dir.FullName, _temp.FullName);
        StringAssert.EndsWith(dir.FullName, Path.Combine("bindings", "winrt"));
    }

    [TestMethod]
    public void ResolveOutputDir_AbsolutePath_InsideWorkspace_Honored()
    {
        var abs = Path.Combine(_temp.FullName, "abs", "out");
        var dir = DynWinrtCodegenService.ResolveOutputDir(_temp, abs);
        Assert.AreEqual(Path.GetFullPath(abs), dir.FullName);
    }

    [TestMethod]
    public void ResolveOutputDir_RejectsAbsolutePathOutsideWorkspace()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.ResolveOutputDir(_temp, @"C:\some\other\place"));
        StringAssert.Contains(ex.Message, "outside the workspace");
    }

    [TestMethod]
    public void ResolveOutputDir_RejectsParentEscape()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.ResolveOutputDir(_temp, "../escape"));
        StringAssert.Contains(ex.Message, "outside the workspace");
    }

    [TestMethod]
    public void ResolveOutputDir_RejectsWorkspaceRootItself()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.ResolveOutputDir(_temp, _temp.FullName));
        StringAssert.Contains(ex.Message, "outside the workspace");
    }

    // -------------------------------------------------------------------------
    // WipeOutputDirSafely — marker presence is the single safety gate.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void WipeOutputDirSafely_NonExistentDir_NoOp()
    {
        var missing = new DirectoryInfo(Path.Combine(_temp.FullName, "missing"));
        DynWinrtCodegenService.WipeOutputDirSafely(missing);
        Assert.IsFalse(missing.Exists);
    }

    [TestMethod]
    public void WipeOutputDirSafely_EmptyDir_NoOpAndPreserved()
    {
        var empty = new DirectoryInfo(Path.Combine(_temp.FullName, "empty"));
        empty.Create();
        DynWinrtCodegenService.WipeOutputDirSafely(empty);
        empty.Refresh();
        Assert.IsTrue(empty.Exists);
    }

    [TestMethod]
    public void WipeOutputDirSafely_NonEmptyWithoutMarker_Throws()
    {
        var dir = new DirectoryInfo(Path.Combine(_temp.FullName, "user-files"));
        dir.Create();
        File.WriteAllText(Path.Combine(dir.FullName, "user.ts"), "// user code");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DynWinrtCodegenService.WipeOutputDirSafely(dir));
        StringAssert.Contains(ex.Message, DynWinrtCodegenService.ManagedMarkerFileName);
        StringAssert.Contains(ex.Message, "Refusing to wipe");

        Assert.IsTrue(File.Exists(Path.Combine(dir.FullName, "user.ts")),
            "User file must be preserved when wipe is refused.");
    }

    [TestMethod]
    public void WipeOutputDirSafely_NonEmptyWithMarker_DeletesAllChildren()
    {
        var dir = new DirectoryInfo(Path.Combine(_temp.FullName, "managed"));
        dir.Create();
        File.WriteAllText(Path.Combine(dir.FullName, DynWinrtCodegenService.ManagedMarkerFileName), "marker");
        File.WriteAllText(Path.Combine(dir.FullName, "Uri.js"), "// generated");
        var subdir = new DirectoryInfo(Path.Combine(dir.FullName, "sub"));
        subdir.Create();
        File.WriteAllText(Path.Combine(subdir.FullName, "Foo.js"), "// generated");

        DynWinrtCodegenService.WipeOutputDirSafely(dir);

        dir.Refresh();
        Assert.IsTrue(dir.Exists, "Wipe deletes children but preserves the directory itself.");
        Assert.AreEqual(0, dir.EnumerateFileSystemInfos().Count(),
            "Marker, generated files, and subdirectories must all be removed so the next run starts clean.");
    }

    // -------------------------------------------------------------------------
    // WriteManagedMarker — file content is debug-only; presence is the contract.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void WriteManagedMarker_CreatesFileNamedDynwinrtManaged()
    {
        var dir = new DirectoryInfo(Path.Combine(_temp.FullName, "out"));
        dir.Create();

        DynWinrtCodegenService.WriteManagedMarker(dir);

        var marker = new FileInfo(Path.Combine(dir.FullName, DynWinrtCodegenService.ManagedMarkerFileName));
        Assert.IsTrue(marker.Exists);
        var body = File.ReadAllText(marker.FullName);
        StringAssert.Contains(body, "generated_at:");
    }

    // -------------------------------------------------------------------------
    // Reparse-point (junction / symlink) rejection. Requires Windows + admin
    // or developer-mode for symlink creation; tests skip otherwise.
    // -------------------------------------------------------------------------

    internal static bool TryCreateJunction(string linkPath, string targetPath, out string? skipReason)
    {
        skipReason = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            skipReason = "Junctions are a Windows-only construct.";
            return false;
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                skipReason = "Could not spawn cmd.exe to create junction.";
                return false;
            }
            p.WaitForExit(10_000);
            if (p.ExitCode != 0)
            {
                skipReason = $"mklink failed (exit {p.ExitCode}): {p.StandardError.ReadToEnd().Trim()}";
                return false;
            }
            return Directory.Exists(linkPath);
        }
        catch (Exception ex)
        {
            skipReason = $"Junction creation threw: {ex.Message}";
            return false;
        }
    }

    [TestMethod]
    public void WipeOutputDirSafely_RejectsReparsePointAsOutputDir()
    {
        var outsideTarget = new DirectoryInfo(Path.Combine(_temp.FullName, "outside"));
        outsideTarget.Create();
        var preciousFile = Path.Combine(outsideTarget.FullName, "precious.txt");
        File.WriteAllText(preciousFile, "DO NOT DELETE");
        File.WriteAllText(Path.Combine(outsideTarget.FullName, DynWinrtCodegenService.ManagedMarkerFileName),
            "# fake marker — would normally authorise wipe");

        var workspace = new DirectoryInfo(Path.Combine(_temp.FullName, "ws"));
        workspace.Create();
        var junction = Path.Combine(workspace.FullName, "bindings");

        if (!TryCreateJunction(junction, outsideTarget.FullName, out var skip))
        {
            Assert.Inconclusive(skip ?? "Junction creation unavailable in this environment.");
            return;
        }

        var outputDir = new DirectoryInfo(junction);
        var threw = false;
        try
        {
            DynWinrtCodegenService.WipeOutputDirSafely(outputDir);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Wipe must refuse a reparse-point output dir.");
        Assert.IsTrue(File.Exists(preciousFile),
            "Precious file behind the junction must remain untouched.");
    }

    [TestMethod]
    public void ResolveOutputDir_RejectsAncestorReparsePoint()
    {
        var outside = new DirectoryInfo(Path.Combine(_temp.FullName, "outside"));
        outside.Create();
        var workspace = new DirectoryInfo(Path.Combine(_temp.FullName, "workspace"));
        workspace.Create();
        var junctionInsideWs = Path.Combine(workspace.FullName, "ws-link");

        if (!TryCreateJunction(junctionInsideWs, outside.FullName, out var skip))
        {
            Assert.Inconclusive(skip ?? "Junction creation unavailable.");
            return;
        }

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            DynWinrtCodegenService.ResolveOutputDir(workspace, "ws-link/out"));
        StringAssert.Contains(ex.Message, "reparse point",
            "Error must call out the reparse-point reason for the rejection.");
    }
}

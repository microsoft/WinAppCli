// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

// Direct tests for the shared PathSafety helper. Pre-r3 the helper was
// only covered indirectly (through ConfigService / WinmdsLockfileService),
// which made it easy for the helper to drift: e.g. the
// pre-r3 implementation used FileInfo.Exists internally, which probes the
// filesystem before the reparse-point flag check — defeating the helper's
// stated purpose. These tests pin the API directly so future edits can't
// silently regress the contract.
[TestClass]
public class PathSafetyTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"PathSafetyTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Teardown()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------------
    // Containment
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_PathEqualsBoundary_ReturnsFalse()
    {
        // The boundary itself is a valid target — callers pass e.g. the
        // workspace dir as both path and boundary when checking the root.
        bool unsafePath = PathSafety.HasReparsePointOnPath(_tempDir.FullName, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "boundary == path is allowed when neither is a reparse point");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PathUnderBoundary_NoReparsePoints_ReturnsFalse()
    {
        var nested = Path.Combine(_tempDir.FullName, "sub", "deeper", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(nested, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "deep child under a clean boundary must pass");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PathOutsideBoundary_ReturnsTrue()
    {
        // A sibling of the boundary is NOT contained — refuse.
        var sibling = Path.Combine(_tempDir.Parent!.FullName, "other-workspace", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(sibling, _tempDir.FullName);
        Assert.IsTrue(unsafePath, "sibling-of-boundary must be rejected (containment violation)");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PathTraversingParent_ReturnsTrue()
    {
        // `..` lets a path escape the boundary; GetFullPath should
        // normalise and containment should then reject.
        var escape = Path.Combine(_tempDir.FullName, "..", "elsewhere", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(escape, _tempDir.FullName);
        Assert.IsTrue(unsafePath, "..-escaping path must be rejected after normalisation");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PrefixCollision_ReturnsTrue()
    {
        // `C:\foo-bar\file` starts with `C:\foo` as a string but is NOT
        // contained — the separator-after-boundary requirement guards
        // against this kind of substring confusion.
        var boundary = Path.Combine(_tempDir.FullName, "foo");
        var nearby = Path.Combine(_tempDir.FullName, "foo-bar", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(nearby, boundary);
        Assert.IsTrue(unsafePath, "substring-prefix collisions must NOT count as containment");
    }

    // ---------------------------------------------------------------------
    // UNC rejection
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_UncBoundary_ReturnsTrue()
    {
        bool unsafePath = PathSafety.HasReparsePointOnPath(
            @"\\server\share\file.txt",
            @"\\server\share");
        Assert.IsTrue(unsafePath, "UNC boundary must be refused outright");
    }

    [TestMethod]
    public void HasReparsePointOnPath_UncPath_ReturnsTrue()
    {
        bool unsafePath = PathSafety.HasReparsePointOnPath(
            @"\\server\share\file.txt",
            _tempDir.FullName);
        Assert.IsTrue(unsafePath, "UNC path must be refused outright");
    }

    [TestMethod]
    public void HasReparsePointOnPath_LongPathPrefixLocal_TreatedAsLocal()
    {
        // \\?\C:\… is the long-path prefix for a local path; NOT UNC.
        // It must NOT be rejected via the UNC check.
        var longPath = @"\\?\" + Path.Combine(_tempDir.FullName, "file.txt");
        var longBoundary = @"\\?\" + _tempDir.FullName;
        bool unsafePath = PathSafety.HasReparsePointOnPath(longPath, longBoundary);
        Assert.IsFalse(unsafePath,
            @"\\?\ long-path prefix on a local drive must NOT be treated as UNC");
    }

    [TestMethod]
    public void HasReparsePointOnPath_LongPathUncPrefix_ReturnsTrue()
    {
        // \\?\UNC\server\share IS UNC, just behind the long-path prefix —
        // must still be refused.
        bool unsafePath = PathSafety.HasReparsePointOnPath(
            @"\\?\UNC\server\share\file.txt",
            @"\\?\UNC\server\share");
        Assert.IsTrue(unsafePath, @"\\?\UNC\ is still UNC and must be refused");
    }

    // ---------------------------------------------------------------------
    // Missing segments
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_MissingLeaf_IsAllowed()
    {
        // Callers (e.g. ConfigService.Save) check the path
        // BEFORE creating the file — a missing leaf must not be rejected.
        var leaf = Path.Combine(_tempDir.FullName, "not-yet-written.yaml");
        Assert.IsFalse(File.Exists(leaf));
        bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "missing leaf must not trigger refusal");
    }

    [TestMethod]
    public void HasReparsePointOnPath_MissingIntermediate_IsAllowed()
    {
        var nested = Path.Combine(_tempDir.FullName, "new", "subdir", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(nested, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "missing intermediates (about to be created) must pass");
    }

    // ---------------------------------------------------------------------
    // Reparse-point detection
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_JunctionBoundary_ReturnsTrue()
    {
        // If the boundary itself is a junction, every descendant probe
        // would silently follow it. We must refuse without ever touching
        // a descendant.
        var junctionParent = Path.Combine(_tempDir.FullName, "real");
        Directory.CreateDirectory(junctionParent);
        var junction = Path.Combine(_tempDir.FullName, "boundary-as-junction");
        if (!TryCreateJunction(junction, junctionParent))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var leaf = Path.Combine(junction, "file.txt");
            bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, junction);
            Assert.IsTrue(unsafePath, "boundary being a junction must be refused");
        }
        finally
        {
            try { Directory.Delete(junction, recursive: false); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void HasReparsePointOnPath_JunctionIntermediate_ReturnsTrue()
    {
        // A junction on an ancestor between boundary and the leaf must
        // also be refused.
        var real = Path.Combine(_tempDir.FullName, "real-target");
        Directory.CreateDirectory(real);
        var junctionDir = Path.Combine(_tempDir.FullName, "linked");
        if (!TryCreateJunction(junctionDir, real))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var leaf = Path.Combine(junctionDir, "file.txt");
            bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, _tempDir.FullName);
            Assert.IsTrue(unsafePath, "junction on the descent path must be refused");
        }
        finally
        {
            try { Directory.Delete(junctionDir, recursive: false); } catch { /* ignore */ }
        }
    }

    // Spawns `cmd /c mklink /J` to create a junction (the only reparse-
    // point creation that does NOT require Developer Mode / admin on a
    // typical CI box). Returns false on any failure so callers can mark
    // the test inconclusive instead of failing.
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Drive-root boundary regression (round-4 M1)
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_DriveRootBoundary_StillRejectsJunctionDescendant()
    {
        // If the boundary is a bare drive root (`C:\`) the descent loop
        // must still call Path.Combine with a rooted prefix — otherwise
        // Path.Combine("C:", "foo") yields a drive-relative "C:foo"
        // (resolved against the per-drive CWD), the wrong inode is
        // probed, and the reparse-point flag is missed. This test pins
        // that the drive-root path is normalized to keep the separator.
        var junctionDir = Path.Combine(_tempDir.FullName, "boundary-drive-root-junction");
        if (!TryCreateJunction(junctionDir, Path.GetTempPath()))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            // Boundary = drive root (e.g. `C:\`). Path is a junction
            // descendant. Pre-r4 this silently returned false (allowed).
            var drive = Path.GetPathRoot(_tempDir.FullName)!;
            var leaf = Path.Combine(junctionDir, "victim.yaml");
            bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, drive);
            Assert.IsTrue(unsafePath, "drive-root boundary must still detect junction on descent");
        }
        finally
        {
            try { Directory.Delete(junctionDir, recursive: false); } catch { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------------
    // AtomicWriteAllTextAsync (round-4 M3)
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_NewFile_WritesContentsAndLeavesNoTempBehind()
    {
        var target = Path.Combine(_tempDir.FullName, "out.yaml");
        const string contents = "key: value\n";

        await PathSafety.AtomicWriteAllTextAsync(target, contents, System.Text.Encoding.UTF8);

        Assert.IsTrue(File.Exists(target), "target file must exist after atomic write");
        Assert.AreEqual(contents, File.ReadAllText(target));
        // No stray sibling temp files left behind on success.
        var siblings = Directory.GetFiles(_tempDir.FullName, "out.yaml.tmp-*");
        Assert.AreEqual(0, siblings.Length, "atomic write must remove its sibling .tmp on success");
    }

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_ExistingFile_OverwritesContents()
    {
        var target = Path.Combine(_tempDir.FullName, "existing.yaml");
        File.WriteAllText(target, "old contents");

        await PathSafety.AtomicWriteAllTextAsync(target, "new contents", System.Text.Encoding.UTF8);

        Assert.AreEqual("new contents", File.ReadAllText(target));
    }

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_DestinationDirMissing_ThrowsAndCleansSiblingTemp()
    {
        // Stage failure: the sibling temp creation calls FileStream with
        // FileMode.CreateNew under a non-existent parent dir, throwing
        // DirectoryNotFoundException. The cleanup branch must still run
        // so we don't leak the .tmp sibling (or in this case, prove that
        // nothing was ever written to the missing dir's parent).
        var missingDir = Path.Combine(_tempDir.FullName, "no-such-dir");
        var target = Path.Combine(missingDir, "out.yaml");

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(async () =>
            await PathSafety.AtomicWriteAllTextAsync(target, "x", System.Text.Encoding.UTF8));

        Assert.IsFalse(Directory.Exists(missingDir), "atomic write must not create parent dirs");
        // No sibling temp under the workspace either (the failure happened
        // before any file existed).
        var stray = Directory.GetFiles(_tempDir.FullName, "*.tmp-*", SearchOption.AllDirectories);
        Assert.AreEqual(0, stray.Length, "no .tmp sibling should be left behind on failure");
    }
}

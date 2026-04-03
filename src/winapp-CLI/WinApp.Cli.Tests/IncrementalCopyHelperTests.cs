// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class IncrementalCopyHelperTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"IncrementalCopyTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    private DirectoryInfo CreateSubDir(string name)
    {
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, name));
        dir.Create();
        return dir;
    }

    private static FileInfo WriteFile(DirectoryInfo dir, string relativePath, string content)
    {
        var path = Path.Combine(dir.FullName, relativePath);
        var file = new FileInfo(path);
        file.Directory?.Create();
        File.WriteAllText(path, content);
        return file;
    }

    #region SyncDirectory Tests

    [TestMethod]
    public void SyncDirectory_FirstSync_CopiesAllFiles()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        WriteFile(source, "app.exe", "exe-content");
        WriteFile(source, "app.dll", "dll-content");
        WriteFile(source, "sub\\lib.dll", "lib-content");

        var result = IncrementalCopyHelper.SyncDirectory(source, dest);

        Assert.AreEqual(3, result.Copied);
        Assert.AreEqual(0, result.Skipped);
        Assert.AreEqual(0, result.Deleted);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "app.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "app.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "sub", "lib.dll")));
    }

    [TestMethod]
    public void SyncDirectory_UnchangedFiles_AreSkipped()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        WriteFile(source, "app.exe", "exe-content");
        WriteFile(source, "app.dll", "dll-content");

        // First sync copies everything
        IncrementalCopyHelper.SyncDirectory(source, dest);

        // Second sync should skip everything (no changes)
        var result = IncrementalCopyHelper.SyncDirectory(source, dest);

        Assert.AreEqual(0, result.Copied);
        Assert.AreEqual(2, result.Skipped);
        Assert.AreEqual(0, result.Deleted);
    }

    [TestMethod]
    public void SyncDirectory_ModifiedFile_IsCopied()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        WriteFile(source, "app.exe", "original");

        // First sync
        IncrementalCopyHelper.SyncDirectory(source, dest);

        // Modify the source file (different content = different size)
        Thread.Sleep(50); // ensure timestamp differs
        WriteFile(source, "app.exe", "modified-content-longer");

        var result = IncrementalCopyHelper.SyncDirectory(source, dest);

        Assert.AreEqual(1, result.Copied);
        Assert.AreEqual(0, result.Skipped);
        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual("modified-content-longer", File.ReadAllText(Path.Combine(dest.FullName, "app.exe")));
    }

    [TestMethod]
    public void SyncDirectory_StaleFile_IsDeleted()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        WriteFile(source, "app.exe", "exe");
        WriteFile(source, "old.dll", "old");

        // First sync copies both
        IncrementalCopyHelper.SyncDirectory(source, dest);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "old.dll")));

        // Remove old.dll from source
        File.Delete(Path.Combine(source.FullName, "old.dll"));

        // Second sync should delete old.dll from dest
        var result = IncrementalCopyHelper.SyncDirectory(source, dest);

        Assert.AreEqual(0, result.Copied);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(1, result.Deleted);
        Assert.IsFalse(File.Exists(Path.Combine(dest.FullName, "old.dll")));
    }

    [TestMethod]
    public void SyncDirectory_ProtectedFiles_AreNotDeleted()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        WriteFile(source, "app.exe", "exe");

        // First sync
        IncrementalCopyHelper.SyncDirectory(source, dest);

        // Manually create protected files in dest that don't exist in source
        WriteFile(dest, "appxmanifest.xml", "<manifest/>");
        WriteFile(dest, "resources.pri", "pri-data");
        WriteFile(dest, "stale.dll", "stale");

        var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "appxmanifest.xml",
            "resources.pri"
        };

        var result = IncrementalCopyHelper.SyncDirectory(source, dest, protectedFiles);

        // stale.dll should be deleted, but protected files should survive
        Assert.AreEqual(1, result.Deleted);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "appxmanifest.xml")));
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "resources.pri")));
        Assert.IsFalse(File.Exists(Path.Combine(dest.FullName, "stale.dll")));
    }

    [TestMethod]
    public void SyncDirectory_NestedOutputInsideSource_IsExcluded()
    {
        var source = CreateSubDir("source");
        var dest = new DirectoryInfo(Path.Combine(source.FullName, "AppX"));
        dest.Create();

        WriteFile(source, "app.exe", "exe");
        // This file is inside the dest folder (nested inside source)
        WriteFile(dest, "should-not-recurse.txt", "nested");

        var result = IncrementalCopyHelper.SyncDirectory(source, dest);

        // Should only copy app.exe, not recurse into dest
        Assert.AreEqual(1, result.Copied);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "app.exe")));
    }

    [TestMethod]
    public void SyncDirectory_CreatesDestDirectory_IfNotExists()
    {
        var source = CreateSubDir("source");
        var dest = new DirectoryInfo(Path.Combine(_tempDir.FullName, "nonexistent_dest"));
        WriteFile(source, "app.exe", "exe");

        Assert.IsFalse(dest.Exists);

        var result = IncrementalCopyHelper.SyncDirectory(source, dest);

        Assert.AreEqual(1, result.Copied);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "app.exe")));
    }

    [TestMethod]
    public void SyncDirectory_SubdirectoriesInSource_AreCopied()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        WriteFile(source, "root.dll", "root");
        WriteFile(source, "runtimes\\win-x64\\native\\lib.dll", "native-lib");
        WriteFile(source, "wwwroot\\index.html", "<html/>");

        var result = IncrementalCopyHelper.SyncDirectory(source, dest);

        Assert.AreEqual(3, result.Copied);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "runtimes", "win-x64", "native", "lib.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "wwwroot", "index.html")));
    }

    #endregion

    #region CopyFiles Tests

    [TestMethod]
    public void CopyFiles_FirstCopy_CopiesAll()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        var file1 = WriteFile(source, "icon.png", "icon-data");
        var file2 = WriteFile(source, "assets\\logo.png", "logo-data");

        var files = new List<(FileInfo, string)>
        {
            (file1, "icon.png"),
            (file2, "assets\\logo.png"),
        };

        var (copied, skipped) = IncrementalCopyHelper.CopyFiles(files, dest);

        Assert.AreEqual(2, copied);
        Assert.AreEqual(0, skipped);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "icon.png")));
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "assets", "logo.png")));
    }

    [TestMethod]
    public void CopyFiles_UnchangedFiles_AreSkipped()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        var file1 = WriteFile(source, "icon.png", "icon-data");

        var files = new List<(FileInfo, string)> { (file1, "icon.png") };

        // First copy
        IncrementalCopyHelper.CopyFiles(files, dest);

        // Second copy should skip
        var (copied, skipped) = IncrementalCopyHelper.CopyFiles(files, dest);

        Assert.AreEqual(0, copied);
        Assert.AreEqual(1, skipped);
    }

    [TestMethod]
    public void CopyFiles_ModifiedFile_IsCopied()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        var file1 = WriteFile(source, "icon.png", "original");

        var files = new List<(FileInfo, string)> { (file1, "icon.png") };

        // First copy
        IncrementalCopyHelper.CopyFiles(files, dest);

        // Modify the source
        Thread.Sleep(50);
        file1 = WriteFile(source, "icon.png", "modified-content-longer");
        files = [(file1, "icon.png")];

        var (copied, skipped) = IncrementalCopyHelper.CopyFiles(files, dest);

        Assert.AreEqual(1, copied);
        Assert.AreEqual(0, skipped);
        Assert.AreEqual("modified-content-longer", File.ReadAllText(Path.Combine(dest.FullName, "icon.png")));
    }

    #endregion
}

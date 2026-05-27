// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class ExecutionAliasResolverTests
{
    // ---- IsSafeAliasName: accept bare filenames --------------------------------

    [TestMethod]
    [DataRow("myapp.exe", DisplayName = "simple .exe")]
    [DataRow("MyApp.exe", DisplayName = "mixed case .exe")]
    [DataRow("my-app.exe", DisplayName = "hyphen")]
    [DataRow("my_app.exe", DisplayName = "underscore")]
    [DataRow("app123.exe", DisplayName = "digits")]
    [DataRow("a.exe", DisplayName = "single letter")]
    [DataRow("notepad", DisplayName = "no extension still ok (resolver does not require .exe)")]
    [DataRow("file.with.dots.exe", DisplayName = "internal dots")]
    public void IsSafeAliasName_ValidBareFilename_ReturnsTrue(string alias)
    {
        Assert.IsTrue(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be accepted as a bare filename");
    }

    // ---- IsSafeAliasName: reject empties / dot-names --------------------------

    [TestMethod]
    [DataRow(null, DisplayName = "null")]
    [DataRow("", DisplayName = "empty")]
    [DataRow("   ", DisplayName = "whitespace only")]
    [DataRow("\t", DisplayName = "tab only")]
    [DataRow(".", DisplayName = "single dot")]
    [DataRow("..", DisplayName = "double dot (parent)")]
    public void IsSafeAliasName_EmptyOrDotNames_ReturnsFalse(string? alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias ?? "<null>"}' to be rejected");
    }

    // ---- IsSafeAliasName: reject path separators ------------------------------

    [TestMethod]
    [DataRow("dir\\evil.exe", DisplayName = "backslash separator")]
    [DataRow("dir/evil.exe", DisplayName = "forward slash separator")]
    [DataRow("..\\evil.exe", DisplayName = "parent traversal with backslash")]
    [DataRow("../evil.exe", DisplayName = "parent traversal with forward slash")]
    [DataRow("a\\b\\c.exe", DisplayName = "nested backslash path")]
    [DataRow("a/b/c.exe", DisplayName = "nested forward slash path")]
    [DataRow("\\evil.exe", DisplayName = "leading backslash (rooted on current drive)")]
    [DataRow("/evil.exe", DisplayName = "leading forward slash")]
    public void IsSafeAliasName_PathSeparators_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — contains a path separator");
    }

    // ---- IsSafeAliasName: reject rooted / absolute paths ----------------------

    [TestMethod]
    [DataRow("C:\\Windows\\System32\\calc.exe", DisplayName = "absolute path with drive")]
    [DataRow("C:evil.exe", DisplayName = "drive-relative path")]
    [DataRow("\\\\server\\share\\evil.exe", DisplayName = "UNC path")]
    [DataRow("\\\\?\\C:\\evil.exe", DisplayName = "extended-length UNC")]
    public void IsSafeAliasName_RootedPaths_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — is a rooted / absolute path");
    }

    // ---- IsSafeAliasName: reject invalid filename characters ------------------

    [TestMethod]
    public void IsSafeAliasName_NullCharacter_ReturnsFalse()
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName("evil\0.exe"));
    }

    [TestMethod]
    [DataRow("evil*.exe", DisplayName = "asterisk")]
    [DataRow("evil?.exe", DisplayName = "question mark")]
    [DataRow("evil<.exe", DisplayName = "less than")]
    [DataRow("evil>.exe", DisplayName = "greater than")]
    [DataRow("evil|.exe", DisplayName = "pipe")]
    [DataRow("evil\".exe", DisplayName = "quote")]
    [DataRow("evil:test.exe", DisplayName = "colon (drive separator / ADS)")]
    public void IsSafeAliasName_InvalidFilenameChars_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — contains an invalid filename character");
    }

    [TestMethod]
    public void IsSafeAliasName_TooLong_ReturnsFalse()
    {
        var alias = new string('a', 256) + ".exe";
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            "Aliases longer than 255 characters should be rejected");
    }

    // ---- ResolveAliasPath: success + base directory behaviour -----------------

    [TestMethod]
    public void ResolveAliasPath_ValidAlias_ReturnsAbsolutePathUnderBaseDirectory()
    {
        var baseDir = @"C:\Users\test\AppData\Local\Microsoft\WindowsApps";

        var result = ExecutionAliasResolver.ResolveAliasPath("myapp.exe", baseDir);

        Assert.IsNotNull(result);
        Assert.AreEqual(Path.Combine(baseDir, "myapp.exe"), result!.FullName);
    }

    [TestMethod]
    public void ResolveAliasPath_DefaultBaseDirectory_UsesWindowsAppsLocation()
    {
        var result = ExecutionAliasResolver.ResolveAliasPath("myapp.exe");

        Assert.IsNotNull(result);
        var expectedBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");
        Assert.AreEqual(Path.Combine(expectedBase, "myapp.exe"), result!.FullName);
    }

    [TestMethod]
    public void GetDefaultWindowsAppsDirectory_ReturnsExpectedPath()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");

        Assert.AreEqual(expected, ExecutionAliasResolver.GetDefaultWindowsAppsDirectory());
    }

    // ---- ResolveAliasPath: refuse hostile inputs ------------------------------

    [TestMethod]
    [DataRow("..\\evil.exe", DisplayName = "parent traversal must not resolve")]
    [DataRow("dir\\evil.exe", DisplayName = "path separator must not resolve")]
    [DataRow("C:\\Windows\\System32\\calc.exe", DisplayName = "absolute path must not resolve")]
    [DataRow("\\\\server\\share\\evil.exe", DisplayName = "UNC must not resolve")]
    [DataRow("", DisplayName = "empty must not resolve")]
    [DataRow(null, DisplayName = "null must not resolve")]
    [DataRow("evil\0.exe", DisplayName = "NUL must not resolve")]
    public void ResolveAliasPath_HostileAlias_ReturnsNull(string? alias)
    {
        var result = ExecutionAliasResolver.ResolveAliasPath(alias, @"C:\WindowsApps");

        Assert.IsNull(result,
            $"Hostile alias '{alias ?? "<null>"}' must not be resolved to a filesystem path");
    }

    // ---- ResolveAliasPath: Exists reports filesystem state --------------------

    [TestMethod]
    public void ResolveAliasPath_NonExistentFile_ReturnsFileInfoWithExistsFalse()
    {
        // Use a base directory and alias guaranteed not to exist on the test agent.
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var alias = $"winappcli-test-{Guid.NewGuid():N}.exe";

        var result = ExecutionAliasResolver.ResolveAliasPath(alias, baseDir);

        Assert.IsNotNull(result);
        Assert.IsFalse(result!.Exists,
            "ResolveAliasPath must not create the file; callers verify existence themselves");
    }

    [TestMethod]
    public void ResolveAliasPath_ExistingFile_ReturnsFileInfoWithExistsTrue()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"winappcli-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        try
        {
            var alias = "fake-alias.exe";
            var fullPath = Path.Combine(baseDir, alias);
            File.WriteAllText(fullPath, string.Empty);

            var result = ExecutionAliasResolver.ResolveAliasPath(alias, baseDir);

            Assert.IsNotNull(result);
            Assert.IsTrue(result!.Exists,
                "FileInfo.Exists should be true when the resolved file is present");
            Assert.AreEqual(fullPath, result.FullName);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}

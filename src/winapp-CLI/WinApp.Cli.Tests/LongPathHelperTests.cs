// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class LongPathHelperTests
{
    private const int MaxPath = 260;

    // C:\ = 3 chars, .txt = 4 chars
    private static readonly string PrefixC = "C:" + Path.DirectorySeparatorChar;
    private const string SuffixTxt = ".txt";

    /// <summary>Creates a local path of exactly <paramref name="targetLength"/> characters.</summary>
    private static string MakeLocalPath(int targetLength)
    {
        var aCount = targetLength - PrefixC.Length - SuffixTxt.Length;
        return PrefixC + new string('a', aCount) + SuffixTxt;
    }

    #region EnsureExtendedLengthPrefix tests

    [TestMethod]
    public void EnsureExtendedLengthPrefix_ShortPath_ReturnsUnchanged()
    {
        var path = @"C:\short\path\file.txt";
        Assert.AreEqual(path, LongPathHelper.EnsureExtendedLengthPrefix(path));
    }

    [TestMethod]
    public void EnsureExtendedLengthPrefix_ExactlyMaxPath_ReturnsUnchanged()
    {
        // A path of exactly MaxPath (260) chars should not get the extended prefix
        var path = MakeLocalPath(MaxPath);
        Assert.AreEqual(MaxPath, path.Length, "Test path should be exactly MaxPath characters");
        Assert.AreEqual(path, LongPathHelper.EnsureExtendedLengthPrefix(path));
    }

    [TestMethod]
    public void EnsureExtendedLengthPrefix_LongLocalPath_AddsPrefix()
    {
        var path = MakeLocalPath(MaxPath + 1);
        Assert.IsTrue(path.Length > MaxPath);
        var result = LongPathHelper.EnsureExtendedLengthPrefix(path);
        Assert.IsTrue(result.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains(path), "Original path should be embedded in result");
    }

    [TestMethod]
    public void EnsureExtendedLengthPrefix_AlreadyPrefixed_ReturnsUnchanged()
    {
        var path = @"\\?\" + new string('a', MaxPath) + ".txt";
        Assert.AreEqual(path, LongPathHelper.EnsureExtendedLengthPrefix(path));
    }

    [TestMethod]
    public void EnsureExtendedLengthPrefix_LongUncPath_AddsUncPrefix()
    {
        var path = @"\\server\share\" + new string('a', MaxPath);
        Assert.IsTrue(path.Length > MaxPath);
        var result = LongPathHelper.EnsureExtendedLengthPrefix(path);
        Assert.IsTrue(result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal),
            @"UNC paths should use the \\?\UNC\ prefix form");
        // \\server\share\... -> \\?\UNC\server\share\...
        Assert.IsTrue(result.Contains(@"server\share\"), "Server and share should be preserved");
    }

    [TestMethod]
    public void EnsureExtendedLengthPrefix_ShortUncPath_ReturnsUnchanged()
    {
        var path = @"\\server\share\file.txt";
        Assert.AreEqual(path, LongPathHelper.EnsureExtendedLengthPrefix(path));
    }

    #endregion

    #region ValidatePathLength tests

    [TestMethod]
    public void ValidatePathLength_ShortPath_DoesNotThrow()
    {
        var path = @"C:\short\path\file.txt";
        LongPathHelper.ValidatePathLength(path); // Should not throw
    }

    [TestMethod]
    public void ValidatePathLength_ExactlyMaxPath_DoesNotThrow()
    {
        var path = MakeLocalPath(MaxPath);
        Assert.AreEqual(MaxPath, path.Length, "Test path should be exactly MaxPath characters");
        LongPathHelper.ValidatePathLength(path); // Should not throw at exactly MaxPath
    }

    [TestMethod]
    public void ValidatePathLength_LongPath_WhenLongPathsDisabled_ThrowsWithActionableMessage()
    {
        var path = MakeLocalPath(MaxPath + 1);
        Assert.IsTrue(path.Length > MaxPath);

        if (!LongPathHelper.IsSystemLongPathEnabled())
        {
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => LongPathHelper.ValidatePathLength(path));
            Assert.IsTrue(ex.Message.Contains("MAX_PATH"), "Error message should mention MAX_PATH");
            Assert.IsTrue(ex.Message.Contains("aka.ms") || ex.Message.Contains("enable-long-paths"),
                "Error message should include actionable guidance");
        }
        else
        {
            // Long paths enabled on this machine -- method should not throw
            LongPathHelper.ValidatePathLength(path);
        }
    }

    #endregion

    #region GetShortPath tests

    [TestMethod]
    public void GetShortPath_ShortPath_ReturnsUnchanged()
    {
        var path = @"C:\short\path\file.txt";
        Assert.AreEqual(path, LongPathHelper.GetShortPath(path));
    }

    [TestMethod]
    public void GetShortPath_ExactlyMaxPath_ReturnsUnchanged()
    {
        // Paths at or below MaxPath should be returned as-is without calling GetShortPathName
        var path = MakeLocalPath(MaxPath);
        Assert.AreEqual(MaxPath, path.Length, "Test path should be exactly MaxPath characters");
        Assert.AreEqual(path, LongPathHelper.GetShortPath(path));
    }

    [TestMethod]
    public void GetShortPath_PathWithTrailingSeparator_PreservesTrailingSeparator()
    {
        // A directory path ending with the platform separator must still end with a separator
        // after GetShortPath processes it (whether or not GetShortPathName succeeds).
        var sep = Path.DirectorySeparatorChar;
        var path = PrefixC + new string('a', MaxPath) + sep;
        Assert.IsTrue(Path.EndsInDirectorySeparator(path), "Test path should end with directory separator");
        Assert.IsTrue(path.Length > MaxPath, "Test path must exceed MaxPath");

        var result = LongPathHelper.GetShortPath(path);

        Assert.IsTrue(Path.EndsInDirectorySeparator(result),
            "GetShortPath must preserve the trailing directory separator");
    }

    [TestMethod]
    public void GetShortPath_DirectoryPathWithoutTrailingSeparator_DoesNotAddSeparator()
    {
        var path = @"C:\short\directory";
        var result = LongPathHelper.GetShortPath(path);
        Assert.IsFalse(Path.EndsInDirectorySeparator(result),
            "GetShortPath should not add a trailing separator when input has none");
    }

    #endregion

    #region GetShortPathOrThrow tests

    [TestMethod]
    public void GetShortPathOrThrow_ShortPath_ReturnsUnchanged()
    {
        var path = @"C:\short\path\file.txt";
        Assert.AreEqual(path, LongPathHelper.GetShortPathOrThrow(path));
    }

    [TestMethod]
    public void GetShortPathOrThrow_ExactlyMaxPath_ReturnsUnchanged()
    {
        var path = MakeLocalPath(MaxPath);
        Assert.AreEqual(MaxPath, path.Length, "Test path should be exactly MaxPath characters");
        Assert.AreEqual(path, LongPathHelper.GetShortPathOrThrow(path));
    }

    [TestMethod]
    public void GetShortPathOrThrow_LongPathThatCannotBeShortened_Throws()
    {
        // A path with a non-existent deeply nested directory cannot be shortened by GetShortPathName.
        // GetShortPath returns the original (still-long) path, so GetShortPathOrThrow must throw.
        var path = MakeLocalPath(MaxPath + 1);
        Assert.IsTrue(path.Length > MaxPath, "Test path must exceed MaxPath");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => LongPathHelper.GetShortPathOrThrow(path));
        Assert.IsTrue(ex.Message.Contains("too long") || ex.Message.Contains("MAX_PATH") || ex.Message.Contains("short"),
            "Error message should describe that the path is too long and cannot be shortened to a usable length");
    }

    #endregion

    #region ValidatePathLength(path, longPathEnabled) tests

    [TestMethod]
    public void ValidatePathLength_TwoArg_LongPathAndDisabled_Throws()
    {
        var path = MakeLocalPath(MaxPath + 1);
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => LongPathHelper.ValidatePathLength(path, longPathEnabled: false));
        Assert.IsTrue(ex.Message.Contains("MAX_PATH"));
    }

    [TestMethod]
    public void ValidatePathLength_TwoArg_LongPathButEnabled_DoesNotThrow()
    {
        var path = MakeLocalPath(MaxPath + 1);
        LongPathHelper.ValidatePathLength(path, longPathEnabled: true); // must not throw when enabled
    }

    [TestMethod]
    public void IsSystemLongPathEnabled_ReturnsWithoutThrowing()
    {
        // Exercises the registry-read happy path; result depends on machine config, so we only
        // assert it completes and yields a bool.
        var enabled = LongPathHelper.IsSystemLongPathEnabled();
        Assert.IsTrue(enabled == true || enabled == false);
    }

    #endregion

    #region StripExtendedPrefix tests

    [TestMethod]
    public void StripExtendedPrefix_UncPrefix_RestoresDoubleBackslash()
    {
        Assert.AreEqual(@"\\server\share\file", LongPathHelper.StripExtendedPrefix(@"\\?\UNC\server\share\file"));
    }

    [TestMethod]
    public void StripExtendedPrefix_LocalPrefix_RemovesPrefix()
    {
        Assert.AreEqual(@"C:\dir\file", LongPathHelper.StripExtendedPrefix(@"\\?\C:\dir\file"));
    }

    [TestMethod]
    public void StripExtendedPrefix_NoPrefix_ReturnsUnchanged()
    {
        Assert.AreEqual(@"C:\dir\file", LongPathHelper.StripExtendedPrefix(@"C:\dir\file"));
    }

    #endregion

    #region GetShortPath(path, shortener) injected-seam tests

    // A long path whose directory portion exceeds MaxPath, so EnsureExtendedLengthPrefix adds a prefix.
    private static readonly string LongLocalDirPath = @"C:\" + new string('d', 300) + @"\f.txt";
    private static readonly string LongUncDirPath = @"\\srv\share\" + new string('d', 300) + @"\f.txt";

    [TestMethod]
    public void GetShortPath_Injected_LocalPrefixedResult_IsStripped()
    {
        var result = LongPathHelper.GetShortPath(LongLocalDirPath, _ => @"\\?\C:\SHORT~1");

        Assert.AreEqual(@"C:\SHORT~1\f.txt", result);
    }

    [TestMethod]
    public void GetShortPath_Injected_UncPrefixedResult_IsStripped()
    {
        var result = LongPathHelper.GetShortPath(LongUncDirPath, _ => @"\\?\UNC\srv\share\S~1");

        Assert.AreEqual(@"\\srv\share\S~1\f.txt", result);
    }

    [TestMethod]
    public void GetShortPath_Injected_NullShortener_ReturnsOriginalDirectoryPath()
    {
        // Shortener signals "cannot shorten" by returning null -> original directory preserved.
        var result = LongPathHelper.GetShortPath(LongLocalDirPath, _ => null);

        Assert.AreEqual(LongLocalDirPath, result);
    }

    [TestMethod]
    public void GetShortPath_Injected_ShortenerThrowsDllNotFound_ReturnsOriginalDirectoryPath()
    {
        var result = LongPathHelper.GetShortPath(LongLocalDirPath, _ => throw new DllNotFoundException("no api"));

        Assert.AreEqual(LongLocalDirPath, result);
    }

    [TestMethod]
    public void GetShortPath_Injected_BareLongFilenameWithoutDirectory_ReturnsOriginal()
    {
        var bareLongName = new string('a', 300) + ".txt";
        var wasCalled = false;

        var result = LongPathHelper.GetShortPath(bareLongName, _ => { wasCalled = true; return "ignored"; });

        Assert.AreEqual(bareLongName, result);
        Assert.IsFalse(wasCalled, "Shortener must not be invoked when there is no directory portion.");
    }

    [TestMethod]
    public void GetShortPath_Production_ExistingShortDirectoryWithLongFilename_InvokesNativeShortener()
    {
        // Exercises the real GetShortPathName success path: the directory exists (temp dir), so the
        // native call returns a non-zero buffer. The filename keeps the total path over MaxPath.
        var tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "lp" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var longName = new string('a', 300) + ".txt";
            var path = Path.Combine(tempDir, longName);
            Assert.IsTrue(path.Length > MaxPath);

            var result = LongPathHelper.GetShortPath(path);

            Assert.IsTrue(result.EndsWith(longName, StringComparison.Ordinal),
                "The long filename must be preserved regardless of directory shortening.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion
}

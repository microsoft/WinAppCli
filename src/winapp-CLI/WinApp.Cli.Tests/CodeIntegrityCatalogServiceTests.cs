// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class CodeIntegrityCatalogServiceTests : BaseCommandTests
{
    private string _testInputDirectory = null!;
    private CodeIntegrityCatalogService _codeIntegrityCatalogService = null!;

    private static void VerifyCdfContent(string content, List<string> files, string expectedCatalogPath, bool usePageHashes, bool computeFlatHashes)
    {
        StringAssert.Contains(content, "[CatalogHeader]");
        StringAssert.Contains(content, $"Name={expectedCatalogPath}");
        StringAssert.Contains(content, "PublicVersion=1");
        StringAssert.Contains(content, "CatalogVersion=2");
        StringAssert.Contains(content, "HashAlgorithms=SHA256");
        StringAssert.Contains(content, "CATATTR1=0x10010001:OSAttr:2:6.2");
        if (usePageHashes)
        {
            StringAssert.Contains(content, "PageHashes=true");
        }
        else
        {
            StringAssert.Contains(content, "PageHashes=false");
        }

        foreach(var file in files)
        {
            StringAssert.Contains(content, $"<HASH>{file}");
            if (computeFlatHashes)
            {
                StringAssert.Contains(content, $"<HASH>{file}ALTSIPID={{DE351A42-8E59-11d0-8C47-00C04FC295EE}}");
            }
        }
    }

    [TestInitialize]
    public void Setup()
    {
        _testInputDirectory = Path.Combine(_tempDirectory.FullName, Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testInputDirectory);
        _codeIntegrityCatalogService = new CodeIntegrityCatalogService(GetRequiredService<ILogger<CodeIntegrityCatalogService>>());
    }

    private static void CopyExecutablesForTest(string destPath)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(destPath, "cmd.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "sort.exe"), Path.Combine(destPath, "sort.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "cacls.exe"), Path.Combine(destPath, "cacls.exe"));

        var subDirectory1 = Path.Combine(destPath, "1");
        Directory.CreateDirectory(subDirectory1);
        File.Copy(Path.Combine(Environment.SystemDirectory, "chkdsk.exe"), Path.Combine(subDirectory1, "chkdsk.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "conhost.exe"), Path.Combine(subDirectory1, "conhost.exe"));

        var subDirectory2 = Path.Combine(destPath, "2");
        Directory.CreateDirectory(subDirectory2);
        File.Copy(Path.Combine(Environment.SystemDirectory, "dllhost.exe"), Path.Combine(subDirectory2, "dllhost.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "fc.exe"), Path.Combine(subDirectory2, "fc.exe"));

        var subDirectory11 = Path.Combine(subDirectory1, "1");
        Directory.CreateDirectory(subDirectory11);
        File.Copy(Path.Combine(Environment.SystemDirectory, "findstr.exe"), Path.Combine(subDirectory11, "findstr.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "label.exe"), Path.Combine(subDirectory11, "label.exe"));
    }

    #region CreateCatalogDefinitionFile direct tests

    [TestMethod]
    public void CreateCatalogDefinitionFile_GeneratesCorrectHeader()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        var content = File.ReadAllText(cdfPath);
        VerifyCdfContent(content, files, outputCatalogPath, false, false);
        File.Delete(cdfPath);
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_PageHashesTrue_ContainsPageHashesTrue()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, true, false);

        var content = File.ReadAllText(cdfPath);
        VerifyCdfContent(content, files, outputCatalogPath, true, false);
        File.Delete(cdfPath);
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_WithFlatHashes_ContainsAltSipId()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, true);

        var content = File.ReadAllText(cdfPath);
        VerifyCdfContent(content, files, outputCatalogPath, false, true);
        File.Delete(cdfPath);
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_WithoutFlatHashes_DoesNotContainAltSipId()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        var content = File.ReadAllText(cdfPath);
        Assert.IsFalse(content.Contains("ALTSIPID", StringComparison.Ordinal),
            "CDF should not contain ALTSIPID when computeFlatHashes is false");
        File.Delete(cdfPath);
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_MultipleFiles_ContainsAllFiles()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\dir1\app1.exe", @"C:\dir2\app2.exe", @"C:\dir3\app3.dll" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        var content = File.ReadAllText(cdfPath);
        VerifyCdfContent(content, files, outputCatalogPath, false, false);
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_EmptyFiles_GeneratesCdfWithNoMembers()
    {
        var files = new List<string>();
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        var content = File.ReadAllText(cdfPath);
        VerifyCdfContent(content, files, outputCatalogPath, false, false);
        var catalogFilesIndex = content.IndexOf("[CatalogFiles]", StringComparison.Ordinal);
        var afterCatalogFiles = content[(catalogFilesIndex + "[CatalogFiles]".Length)..].Trim();
        Assert.AreEqual(string.Empty, afterCatalogFiles, "No file entries should be present");
    }

    #endregion

    #region CreateExternalCatalog validation tests

    [TestMethod]
    public async Task CreateExternalCatalog_NullDirectories_ThrowsArgumentException()
    {
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync(null!, false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_EmptyDirectories_ThrowsArgumentException()
    {
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync([], false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));
        var dirs = new List<string> { Path.Combine(_tempDirectory.FullName, "nonexistent") };

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync(dirs, false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_NoExecutableFiles_ThrowsInvalidOperationException()
    {
        File.WriteAllText(Path.Combine(_testInputDirectory, "readme.txt"), "not an executable");
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync([_testInputDirectory], false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_OutputAlreadyExistsWithErrorMode_ThrowsIOException()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        File.WriteAllText(outputPath, "existing");
        var output = new FileInfo(outputPath);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync([_testInputDirectory], false, false, false, IfExists.Error, output));
    }

    #endregion

    #region CreateExternalCatalog integration tests

    [TestMethod]
    public async Task CreateExternalCatalog_GeneratesCatalogFile()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Error, new FileInfo(outputPath));

        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
        Assert.IsGreaterThan(0, new FileInfo(outputPath).Length, "Catalog file should not be empty");
    }

    [TestMethod]
    public async Task CreateExternalCatalog_OverwriteMode_ReplacesExistingCatalog()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        File.WriteAllText(outputPath, "old content");
        var oldFileLength = new FileInfo(outputPath).Length;

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Overwrite, new FileInfo(outputPath));

        Assert.IsTrue(File.Exists(outputPath), "Catalog file should exist");
        var newFileLength = new FileInfo(outputPath).Length;
        Assert.AreNotEqual(newFileLength, oldFileLength, "Catalog should be overwritten");
    }

    [TestMethod]
    public async Task CreateExternalCatalog_RecursiveMode_FindsFilesInSubdirectories()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.AllDirectories))
        {
            files.Add(file);
        }

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], true, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, files, outputPath, false, false);
    }

    [TestMethod]
    public async Task CreateExternalCatalog_NonRecursive_SkipsSubdirectoryFiles()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");

        var rootDirectoryFiles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.TopDirectoryOnly))
        {
            rootDirectoryFiles.Add(file);
        }

        var subDirectoryFiles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.AllDirectories))
        {
            if (!rootDirectoryFiles.Contains(file))
            {
                subDirectoryFiles.Add(file);
            }
        }

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, rootDirectoryFiles, outputPath, false, false);

        foreach (var subFile in subDirectoryFiles)
        {
            Assert.IsFalse(cdfContent.Contains(subFile, StringComparison.Ordinal),
                "CDF should not contain files from subdirectories in non-recursive mode");
        }
    }

    [TestMethod]
    public async Task CreateExternalCatalog_SkipsNonExecutableFiles()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var nonExecutableFiles = new List<string>
        {
            Path.Combine(_testInputDirectory, "readme.txt"),
            Path.Combine(_testInputDirectory, "data.json")
        };

        foreach (var file in nonExecutableFiles)
        {
            File.WriteAllText(file, "not executable");
        }


        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (!nonExecutableFiles.Contains(file))
            {
                files.Add(file);
            }
        }

        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, files, outputPath, false, false);
        Assert.IsFalse(cdfContent.Contains("readme.txt", StringComparison.Ordinal),
            "CDF should not contain non-executable files");
        Assert.IsFalse(cdfContent.Contains("data.json", StringComparison.Ordinal),
            "CDF should not contain non-executable files");
    }

    [TestMethod]
    public async Task CreateExternalCatalog_MultipleDirectories_ProcessesAll()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var dir1 = Path.Combine(_testInputDirectory, "1");
        var dir2 = Path.Combine(_testInputDirectory, "2");
        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_testInputDirectory, "test.cat");

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir1, "*.*", SearchOption.TopDirectoryOnly))
        {
            files.Add(file);
        }

        foreach (var file in Directory.EnumerateFiles(dir2, "*.*", SearchOption.TopDirectoryOnly))
        {
            files.Add(file);
        }

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [dir1, dir2], false, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, files, outputPath, false, false);
    }

    #endregion
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class CreateExternalCatalogCommandTests : BaseCommandTests
{
    private string _testInputDirectory = null!;

    [TestInitialize]
    public void Setup()
    {
        _testInputDirectory = Path.Combine(_tempDirectory.FullName, "input");
        Directory.CreateDirectory(_testInputDirectory);
    }

    private static void CopyExecutablesForTest(string destPath)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(destPath, "cmd.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "cacls.exe"), Path.Combine(destPath, "cacls.exe"));
    }

    private static void CopyExecutablesWithSubdirectories(string destPath)
    {
        CopyExecutablesForTest(destPath);
        var subDir = Path.Combine(destPath, "sub");
        Directory.CreateDirectory(subDir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "chkdsk.exe"), Path.Combine(subDir, "chkdsk.exe"));
    }

    #region Argument and option parsing tests

    [TestMethod]
    public void ParseArguments_InputFolder_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        // Act
        var parseResult = command.Parse([_testInputDirectory]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        var inputFolder = parseResult.GetValue(CreateExternalCatalogCommand.InputFolderArgument);
        Assert.AreEqual(_testInputDirectory, inputFolder);
    }

    [TestMethod]
    public void ParseArguments_AllOptions_AreParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "custom.cat");
        var args = new[]
        {
            _testInputDirectory,
            "--recursive",
            "--use-page-hashes",
            "--compute-flat-hashes",
            "--if-exists", "Overwrite",
            "--output", outputPath
        };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(CreateExternalCatalogCommand.RecursiveOption));
        Assert.IsTrue(parseResult.GetValue(CreateExternalCatalogCommand.UsePageHashesOption));
        Assert.IsTrue(parseResult.GetValue(CreateExternalCatalogCommand.ComputeFlatHashesOption));
        Assert.AreEqual(IfExists.Overwrite, parseResult.GetValue(CreateExternalCatalogCommand.IfExistsOption));
    }

    [TestMethod]
    public void ParseArguments_ShortOptions_AreParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "custom.cat");
        var args = new[]
        {
            _testInputDirectory,
            "-r",
            "-o", outputPath
        };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(CreateExternalCatalogCommand.RecursiveOption));
    }

    [TestMethod]
    public void ParseArguments_IfExistsDefaultsToError()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        // Act
        var parseResult = command.Parse([_testInputDirectory]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.AreEqual(IfExists.Error, parseResult.GetValue(CreateExternalCatalogCommand.IfExistsOption));
    }

    [TestMethod]
    public void ParseArguments_BooleanOptionsDefaultToFalse()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        // Act
        var parseResult = command.Parse([_testInputDirectory]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(CreateExternalCatalogCommand.RecursiveOption));
        Assert.IsFalse(parseResult.GetValue(CreateExternalCatalogCommand.UsePageHashesOption));
        Assert.IsFalse(parseResult.GetValue(CreateExternalCatalogCommand.ComputeFlatHashesOption));
    }

    [TestMethod]
    public void ParseArguments_MissingInputFolder_ProducesError()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        // Act
        var parseResult = command.Parse([]);

        // Assert
        Assert.IsNotEmpty(parseResult.Errors, "Should have a parsing error for missing input-folder argument");
    }

    [TestMethod]
    [DataRow("Error", IfExists.Error)]
    [DataRow("Overwrite", IfExists.Overwrite)]
    [DataRow("Skip", IfExists.Skip)]
    public void ParseArguments_IfExistsValues_AreParsedCorrectly(string value, IfExists expected)
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        // Act
        var parseResult = command.Parse([_testInputDirectory, "--if-exists", value]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.AreEqual(expected, parseResult.GetValue(CreateExternalCatalogCommand.IfExistsOption));
    }

    #endregion

    #region Command execution tests

    [TestMethod]
    public async Task Execute_WithValidDirectory_ReturnsZeroExitCode()
    {
        // Arrange
        CopyExecutablesForTest(_testInputDirectory);
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var args = new[] { _testInputDirectory, "--output", outputPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed with valid directory containing executables");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
    }

    [TestMethod]
    public async Task Execute_WithNonExistentDirectory_ReturnsNonZeroExitCode()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var nonExistentDir = Path.Combine(_tempDirectory.FullName, "nonexistent");
        var args = new[] { nonExistentDir, "--output", Path.Combine(_tempDirectory.FullName, "test.cat") };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail for non-existent directory");
    }

    [TestMethod]
    public async Task Execute_WithNoExecutableFiles_ReturnsNonZeroExitCode()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testInputDirectory, "readme.txt"), "not an executable");
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var args = new[] { _testInputDirectory, "--output", Path.Combine(_tempDirectory.FullName, "test.cat") };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when no executable files are found");
    }

    [TestMethod]
    public async Task Execute_OutputAlreadyExists_WithErrorMode_ReturnsNonZeroExitCode()
    {
        // Arrange
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "existing.cat");
        File.WriteAllText(outputPath, "existing content");
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var args = new[] { _testInputDirectory, "--output", outputPath, "--if-exists", "Error" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when output exists and if-exists is Error");
    }

    [TestMethod]
    public async Task Execute_OutputAlreadyExists_WithOverwriteMode_ReturnsZeroExitCode()
    {
        // Arrange
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "existing.cat");
        File.WriteAllText(outputPath, "old");
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var args = new[] { _testInputDirectory, "--output", outputPath, "--if-exists", "Overwrite" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed when output exists and if-exists is Overwrite");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should exist");
        Assert.AreNotEqual("old", File.ReadAllText(outputPath), "Catalog file should be overwritten");
    }

    [TestMethod]
    public async Task Execute_SemicolonSeparatedDirectories_ProcessesAll()
    {
        // Arrange
        var dir1 = Path.Combine(_tempDirectory.FullName, "dir1");
        var dir2 = Path.Combine(_tempDirectory.FullName, "dir2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(dir1, "cmd.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "cacls.exe"), Path.Combine(dir2, "cacls.exe"));

        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var args = new[] { $"{dir1};{dir2}", "--output", outputPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed with semicolon-separated directories");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
    }

    [TestMethod]
    public async Task Execute_WithRecursiveOption_FindsFilesInSubdirectories()
    {
        // Arrange
        CopyExecutablesWithSubdirectories(_testInputDirectory);
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var args = new[] { _testInputDirectory, "--recursive", "--output", outputPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed with recursive option");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
    }

    [TestMethod]
    public async Task Execute_WithPageHashesOption_Succeeds()
    {
        // Arrange
        CopyExecutablesForTest(_testInputDirectory);
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var args = new[] { _testInputDirectory, "--use-page-hashes", "--output", outputPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed with page hashes option");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
    }

    [TestMethod]
    public async Task Execute_WithComputeFlatHashesOption_Succeeds()
    {
        // Arrange
        CopyExecutablesForTest(_testInputDirectory);
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var args = new[] { _testInputDirectory, "--compute-flat-hashes", "--output", outputPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed with compute flat hashes option");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
    }

    [TestMethod]
    public async Task Execute_DefaultOutput_UsesCodeIntegrityExternalCat()
    {
        // Arrange
        CopyExecutablesForTest(_testInputDirectory);
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var args = new[] { _testInputDirectory };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed with default output path");
        var defaultOutput = Path.Combine(_tempDirectory.FullName, CodeIntegrityCatalogService.DefaultCatalogFileName);
        Assert.IsTrue(File.Exists(defaultOutput), $"Default catalog file should be generated at {defaultOutput}");
    }

    [TestMethod]
    public async Task Execute_ErrorPath_LogsErrorToStdErr()
    {
        // Arrange
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var nonExistentDir = Path.Combine(_tempDirectory.FullName, "nonexistent");
        var args = new[] { nonExistentDir, "--output", Path.Combine(_tempDirectory.FullName, "test.cat") };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(1, exitCode);
        var errorOutput = ConsoleStdErr.ToString();
        Assert.Contains("Error generating", errorOutput, "Error message should be logged to stderr");
    }

    [TestMethod]
    public async Task Execute_SuccessPath_GeneratesCatalogFile()
    {
        // Arrange
        CopyExecutablesForTest(_testInputDirectory);
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var args = new[] { _testInputDirectory, "--output", outputPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
        Assert.IsGreaterThan(0, new FileInfo(outputPath).Length, "Catalog file should not be empty");
    }

    [TestMethod]
    public async Task Execute_AllCombinedOptions_Succeeds()
    {
        // Arrange
        CopyExecutablesWithSubdirectories(_testInputDirectory);
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "combined.cat");
        var args = new[]
        {
            _testInputDirectory,
            "-r",
            "--use-page-hashes",
            "--compute-flat-hashes",
            "--if-exists", "Overwrite",
            "-o", outputPath
        };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed with all options combined");
        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
    }

    #endregion
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console.Testing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
#pragma warning disable CA1001 // Disposable fields cleaned up in TestCleanup
public class CrashDumpServiceTests
{
    private TestConsole _console = null!;
    private ILogger<CrashDumpService> _logger = null!;
    private CrashDumpService _service = null!;
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug)).CreateLogger<CrashDumpService>();
        _service = new CrashDumpService(_console, _logger);
        _tempDir = Path.Combine(Path.GetTempPath(), $"CrashDumpTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _console?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_NonExistentDump_ShowsFailureMessage()
    {
        // Arrange
        var dumpPath = Path.Combine(_tempDir, "nonexistent.dmp");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert
        var output = _console.Output;
        Assert.IsTrue(output.Contains("Analysis failed"), $"Expected failure message in output: {output}");
        Assert.IsTrue(output.Contains("windbg"), $"Expected WinDbg fallback suggestion in output: {output}");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDumpFile_ShowsFailureMessage()
    {
        // Arrange — a file that exists but is not a valid dump
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "this is not a valid dump file");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert
        var output = _console.Output;
        Assert.IsTrue(output.Contains("Analysis failed"), $"Expected failure message in output: {output}");
        Assert.IsTrue(output.Contains("windbg"), $"Expected WinDbg fallback suggestion in output: {output}");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDump_WritesLogPath()
    {
        // Arrange
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "not a dump");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert — TestConsole wraps long paths, so check for the filename
        var output = _console.Output;
        Assert.IsTrue(output.Contains("test.log"), $"Expected log filename in output: {output}");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDump_ShowsDumpPath()
    {
        // Arrange
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "not a dump");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert — TestConsole wraps long paths, so check for the filename
        var output = _console.Output;
        Assert.IsTrue(output.Contains("invalid.dmp"), $"Expected dump filename in output: {output}");
    }
}

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
    private FakeXamlTriageService _xamlTriage = null!;
    private CrashDumpService _service = null!;
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug)).CreateLogger<CrashDumpService>();
        _xamlTriage = new FakeXamlTriageService();
        _service = new CrashDumpService(_console, _logger, _xamlTriage);
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

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDump_DoesNotRunWinUiTriage()
    {
        // Arrange — an unreadable dump can't be inspected for WinUI modules, so triage must be skipped.
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "not a dump");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert
        Assert.AreEqual(0, _xamlTriage.AnalyzeCalls.Count, "WinUI triage must not run for an unreadable/non-WinUI dump.");
    }

    [TestMethod]
    public async Task RunXamlTriageGuardedAsync_TriageThrows_ReturnsNoneAndDoesNotPropagate()
    {
        // Fail-open contract: even if the triage service throws (e.g. an internal HttpClient timeout
        // surfacing as OperationCanceledException on a slow first-run download), the crash-analysis flow
        // must not be derailed — otherwise the already-computed managed crash stack is discarded as
        // "Analysis failed." This guards the H1 regression.
        _xamlTriage.ThrowOnAnalyze = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var result = await _service.RunXamlTriageGuardedAsync(Path.Combine(_tempDir, "any.dmp"), useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.None, result.Outcome, "A thrown triage failure must degrade to None, not propagate.");
        Assert.AreEqual(1, _xamlTriage.AnalyzeCalls.Count);
    }

    [TestMethod]
    public async Task RunXamlTriageGuardedAsync_TriageSucceeds_PassesResultThrough()
    {
        _xamlTriage.FakeResult = XamlTriageResult.Succeeded("full breakdown", "0xc000027b — boom");

        var result = await _service.RunXamlTriageGuardedAsync(Path.Combine(_tempDir, "any.dmp"), useSymbols: true);

        Assert.AreEqual(XamlTriageOutcome.Succeeded, result.Outcome);
        Assert.AreEqual("0xc000027b — boom", result.Verdict);
    }

    [TestMethod]
    public void SelectExceptionRecord_StowedWithParameters_UsesStowedRecord()
    {
        var (code, address, useStowed) = CrashDumpService.SelectExceptionRecord(
            savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: 0x1000,
            crashExceptionCode: unchecked((int)0xC000027B), crashExceptionAddress: 0x2000,
            crashExceptionParameters: [0xDEAD, 1]);

        Assert.IsTrue(useStowed, "A stowed exception with parameters must drive the dump's exception record.");
        Assert.AreEqual(unchecked((int)0xC000027B), code);
        Assert.AreEqual((nuint)0x2000, address);
    }

    [TestMethod]
    public void SelectExceptionRecord_StowedWithoutParameters_FallsBackToFirstChance()
    {
        var (code, address, useStowed) = CrashDumpService.SelectExceptionRecord(
            savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: 0x1000,
            crashExceptionCode: unchecked((int)0xC000027B), crashExceptionAddress: 0x2000,
            crashExceptionParameters: null);

        Assert.IsFalse(useStowed, "Without stowed parameters there is nothing for !xamlstowed to read, so keep the first-chance record.");
        Assert.AreEqual(unchecked((int)0xC0000005), code);
        Assert.AreEqual((nuint)0x1000, address);
    }

    [TestMethod]
    public void SelectExceptionRecord_NonStowedCrash_UsesFirstChance()
    {
        var (code, address, useStowed) = CrashDumpService.SelectExceptionRecord(
            savedExceptionCode: unchecked((int)0xE0434352), savedExceptionAddress: 0x1000,
            crashExceptionCode: unchecked((int)0xC0000005), crashExceptionAddress: 0x2000,
            crashExceptionParameters: [0xDEAD, 1]);

        Assert.IsFalse(useStowed, "A non-stowed terminating exception must not replace the record, even with parameters.");
        Assert.AreEqual(unchecked((int)0xE0434352), code);
        Assert.AreEqual((nuint)0x1000, address);
    }
}

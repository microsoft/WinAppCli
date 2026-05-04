// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class GroupableTaskErrorReportingTests
{
    private static GroupableTask<(int ReturnCode, string CompletedMessage)> CreateTupleTask(
        Func<TaskContext, CancellationToken, Task<(int, string)>> body,
        ILogger logger)
    {
        return new GroupableTask<(int ReturnCode, string CompletedMessage)>(
            "test",
            null,
            body,
            new TestConsole(),
            logger,
            new Lock());
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_ExceptionMessageSurfacedInItem2()
    {
        var task = CreateTupleTask(
            (_, _) => throw new InvalidOperationException("boom"),
            NullLogger.Instance);

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.IsFalse(task.SuccessfullyCompleted!.Value);
        Assert.AreEqual(1, result.ReturnCode);
        Assert.AreEqual("boom", result.CompletedMessage);
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_FallsBackToTypeNameWhenMessageBlank()
    {
        var task = CreateTupleTask(
            (_, _) => throw new InvalidOperationException(""),
            NullLogger.Instance);

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.AreEqual(1, result.ReturnCode);
        StringAssert.Contains(result.CompletedMessage, nameof(InvalidOperationException));
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_StackAppendedOnlyAtDebugLevel()
    {
        // Information-level logger: stack should NOT be appended
        var infoTask = CreateTupleTask(
            (_, _) => throw new InvalidOperationException("boom"),
            new MinLevelLogger(LogLevel.Information));
        var infoResult = await infoTask.ExecuteAsync(null, CancellationToken.None);
        Assert.AreEqual("boom", infoResult.CompletedMessage,
            "Stack trace must not be appended when Debug isn't enabled");

        // Debug-level logger: stack SHOULD be appended
        var debugTask = CreateTupleTask(
            (_, _) => throw new InvalidOperationException("boom"),
            new MinLevelLogger(LogLevel.Debug));
        var debugResult = await debugTask.ExecuteAsync(null, CancellationToken.None);
        StringAssert.StartsWith(debugResult.CompletedMessage, "boom");
        Assert.IsTrue(debugResult.CompletedMessage.Length > "boom".Length,
            "Debug-level should include stack trace text");
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_SuccessPathLeavesItem2Untouched()
    {
        var task = CreateTupleTask(
            (_, _) => Task.FromResult((0, "ok")),
            NullLogger.Instance);

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.IsTrue(task.SuccessfullyCompleted!.Value);
        Assert.AreEqual(0, result.ReturnCode);
        Assert.AreEqual("ok", result.CompletedMessage);
    }

    /// <summary>Minimal ILogger that only filters by level; ignores writes.</summary>
    private sealed class MinLevelLogger(LogLevel min) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= min;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

[TestClass]
public class StatusServiceErrorMessageFallbackTests
{
    /// <summary>
    /// Captures log entries so we can verify the user-visible error rendering.
    /// </summary>
    private sealed class CapturingLogger : ILogger<StatusService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public LogLevel MinLevel { get; set; } = LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_NullCompletedMessage_LogsFallbackText()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger();
        var svc = new StatusService(console, logger);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, (string)null!)),
            CancellationToken.None);

        Assert.AreEqual(1, rc);
        Assert.IsTrue(
            logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message == "Operation failed without an error message."),
            "Expected fallback message when CompletedMessage is null");
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_WhitespaceCompletedMessage_LogsFallbackText()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger();
        var svc = new StatusService(console, logger);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, "   ")),
            CancellationToken.None);

        Assert.AreEqual(1, rc);
        Assert.IsTrue(
            logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message == "Operation failed without an error message."));
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_RealCompletedMessage_PassesItThrough()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger();
        var svc = new StatusService(console, logger);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((2, "explicit failure")),
            CancellationToken.None);

        Assert.AreEqual(2, rc);
        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message == "explicit failure"));
        Assert.IsFalse(logger.Entries.Any(e => e.Message == "Operation failed without an error message."),
            "Fallback must not appear when a real message is present");
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_NonDebugLevel_EmitsVerboseHint()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger { MinLevel = LogLevel.Information };
        var svc = new StatusService(console, logger);

        await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, "msg")),
            CancellationToken.None);

        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("--verbose")),
            "Verbose hint should be emitted when not at Debug level");
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_DebugLevel_DoesNotEmitVerboseHint()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger { MinLevel = LogLevel.Debug };
        var svc = new StatusService(console, logger);

        await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, "msg")),
            CancellationToken.None);

        Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("--verbose")),
            "Verbose hint must be suppressed at Debug level");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class ForegroundGuardTests
{
    [TestInitialize]
    public void Initialize() => ResetSeams();

    [TestCleanup]
    public void Cleanup() => ResetSeams();

    [TestMethod]
    public void ForegroundBelongsTo_ReturnsFalseForZeroNullForegroundOrUnrelatedRoot()
    {
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(0));

        ForegroundGuard.s_getForegroundWindow = () => HWND.Null;
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(10));

        ForegroundGuard.s_getForegroundWindow = () => new HWND(20);
        ForegroundGuard.s_getRootAncestor = _ => HWND.Null;
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(10));

        ForegroundGuard.s_getRootAncestor = _ => new HWND(30);
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(10));
    }

    [TestMethod]
    public void ForegroundBelongsTo_AcceptsExactTargetOrRootAncestor()
    {
        ForegroundGuard.s_getForegroundWindow = () => new HWND(10);
        Assert.IsTrue(ForegroundGuard.ForegroundBelongsTo(10));

        ForegroundGuard.s_getForegroundWindow = () => new HWND(20);
        ForegroundGuard.s_getRootAncestor = hwnd =>
        {
            Assert.AreEqual((nint)10, (nint)hwnd);
            return new HWND(20);
        };

        Assert.IsTrue(ForegroundGuard.ForegroundBelongsTo(10));
    }

    [TestMethod]
    public void NoInteractiveDesktop_ReflectsNullForeground()
    {
        ForegroundGuard.s_getForegroundWindow = () => HWND.Null;
        Assert.IsTrue(ForegroundGuard.NoInteractiveDesktop());

        ForegroundGuard.s_getForegroundWindow = () => new HWND(1);
        Assert.IsFalse(ForegroundGuard.NoInteractiveDesktop());
    }

    [TestMethod]
    public void Classify_CoversAllCombinations()
    {
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: false, targetIsForeground: false, anyForegroundWindow: false));
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: true, anyForegroundWindow: false));
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.NoInteractiveDesktop, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: false, anyForegroundWindow: false));
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.ForegroundNotTarget, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: false, anyForegroundWindow: true));
    }

    [TestMethod]
    public void TryEnsureForeground_ProceedsWhenNoTargetOrTargetMatches()
    {
        var logger = new CapturingLogger();
        ForegroundGuard.s_getForegroundWindow = () => new HWND(10);

        Assert.IsTrue(ForegroundGuard.TryEnsureForeground(0, logger, json: false, action: "click"));
        Assert.IsTrue(ForegroundGuard.TryEnsureForeground(10, logger, json: false, action: "click"));
        Assert.AreEqual(0, logger.Messages.Count);
    }

    [TestMethod]
    public void TryEnsureForeground_ReportsNoInteractiveDesktop()
    {
        var logger = new CapturingLogger();
        ForegroundGuard.s_getForegroundWindow = () => HWND.Null;

        Assert.IsFalse(ForegroundGuard.TryEnsureForeground(10, logger, json: false, action: "drag"));

        Assert.AreEqual(LogLevel.Error, logger.Messages.Single().Level);
        StringAssert.Contains(logger.Messages.Single().Message, "No interactive desktop");
    }

    [TestMethod]
    public void TryEnsureForeground_ReportsForegroundNotTargetWithAction()
    {
        var logger = new CapturingLogger();
        ForegroundGuard.s_getForegroundWindow = () => new HWND(20);
        ForegroundGuard.s_getRootAncestor = _ => new HWND(30);

        Assert.IsFalse(ForegroundGuard.TryEnsureForeground(10, logger, json: false, action: "scroll --wheel"));

        Assert.AreEqual(LogLevel.Error, logger.Messages.Single().Level);
        StringAssert.Contains(logger.Messages.Single().Message, "refusing to scroll --wheel");
    }

    private static void ResetSeams()
    {
        ForegroundGuard.s_getForegroundWindow = () => new HWND(1);
        ForegroundGuard.s_getRootAncestor = _ => HWND.Null;
    }
}

internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

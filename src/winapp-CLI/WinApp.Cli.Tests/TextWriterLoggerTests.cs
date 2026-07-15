// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Text;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class TextWriterLoggerTests
{
    private static (TextWriterLoggerProvider Provider, StringWriter Out, StringWriter Err) NewProvider()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        return (new TextWriterLoggerProvider(stdout, stderr), stdout, stderr);
    }

    [TestMethod]
    public void Provider_NullStdout_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new TextWriterLoggerProvider(null!, new StringWriter()));
    }

    [TestMethod]
    public void Provider_NullStderr_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new TextWriterLoggerProvider(new StringWriter(), null!));
    }

    [TestMethod]
    public void Provider_Dispose_DoesNotThrow()
    {
        var (provider, _, _) = NewProvider();
        provider.Dispose(); // writers are owned by the caller; Dispose is a safe no-op
    }

    [TestMethod]
    public void IsEnabled_NoneIsDisabled_OthersEnabled()
    {
        var (provider, _, _) = NewProvider();
        var logger = provider.CreateLogger("cat");

        Assert.IsFalse(logger.IsEnabled(LogLevel.None));
        Assert.IsTrue(logger.IsEnabled(LogLevel.Information));
        Assert.IsTrue(logger.IsEnabled(LogLevel.Error));
    }

    [TestMethod]
    public void BeginScope_WithoutScopeProvider_ReturnsDisposableNullScope()
    {
        var (provider, _, _) = NewProvider();
        var logger = provider.CreateLogger("cat");

        using var scope = logger.BeginScope("no-op");

        Assert.IsNotNull(scope, "BeginScope must return a non-null disposable even with no scope provider.");
        scope.Dispose(); // NullScope.Dispose is a no-op and must be safe to call.
    }

    [TestMethod]
    public void Log_LevelNone_IsSuppressed_WritesNothing()
    {
        var (provider, stdout, stderr) = NewProvider();
        var logger = provider.CreateLogger("cat");

        logger.Log(LogLevel.None, new EventId(1), "ignored", exception: null, (state, _) => state);

        Assert.AreEqual(string.Empty, stdout.ToString());
        Assert.AreEqual(string.Empty, stderr.ToString());
    }

    [TestMethod]
    public void Log_ErrorLevel_GoesToStderrWithLevelPrefix()
    {
        var (provider, _, stderr) = NewProvider();
        var logger = provider.CreateLogger("cat");

        logger.LogError("disk full");

        var err = stderr.ToString();
        Assert.IsTrue(err.Contains("[ERROR] - ", StringComparison.Ordinal), $"Expected level prefix, got: {err}");
        Assert.IsTrue(err.Contains("disk full", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Log_ErrorWithScopeAndException_AppendsScopeAndExceptionToStderr()
    {
        var (provider, _, stderr) = NewProvider();
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("cat");

        using (logger.BeginScope("OUTER_SCOPE"))
        {
            logger.LogError(new InvalidOperationException("BOOM_MESSAGE"), "operation failed");
        }

        var err = stderr.ToString();
        Assert.IsTrue(err.Contains("operation failed", StringComparison.Ordinal));
        Assert.IsTrue(err.Contains("=> OUTER_SCOPE", StringComparison.Ordinal), $"Scope must be appended, got: {err}");
        Assert.IsTrue(err.Contains("BOOM_MESSAGE", StringComparison.Ordinal), $"Exception must be appended, got: {err}");
    }

    [TestMethod]
    public void AddTextWriterLogger_EndToEnd_WritesErrorToStderrWriter()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddTextWriterLogger(stdout, stderr);
        });
        var logger = factory.CreateLogger("integration");

        logger.LogError("boom via factory");

        Assert.IsTrue(stderr.ToString().Contains("boom via factory", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OutputCapture_Encoding_IsAscii()
    {
        using var capture = new OutputCapture(new StringWriter());
        Assert.AreEqual(Encoding.ASCII, capture.Encoding);
    }

    [TestMethod]
    public void OutputCapture_WriteAndWriteLine_MirrorToInnerAndCapture()
    {
        var inner = new StringWriter();
        using var capture = new OutputCapture(inner);

        capture.Write("alpha");
        capture.WriteLine("beta");

        var captured = capture.ToString();
        var mirrored = inner.ToString();
        Assert.IsTrue(captured.Contains("alpha", StringComparison.Ordinal));
        Assert.IsTrue(captured.Contains("beta", StringComparison.Ordinal));
        Assert.IsTrue(mirrored.Contains("alpha", StringComparison.Ordinal));
        Assert.IsTrue(mirrored.Contains("beta", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OutputCapture_Clear_EmptiesCapturedBufferOnly()
    {
        var inner = new StringWriter();
        var capture = new OutputCapture(inner);

        capture.Write("to-be-cleared");
        capture.Clear();

        Assert.AreEqual(string.Empty, capture.ToString(), "Clear must empty the captured buffer.");
        Assert.IsTrue(inner.ToString().Contains("to-be-cleared", StringComparison.Ordinal),
            "Clear must not affect what was already mirrored to the inner writer.");
    }

    [TestMethod]
    public void OutputCapture_Dispose_DisposesInnerWriter()
    {
        var inner = new StringWriter();
        var capture = new OutputCapture(inner);

        capture.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => inner.Write("after dispose"));
    }
}

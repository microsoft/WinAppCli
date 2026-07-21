// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class MakeAppxToolTests
{
    [TestMethod]
    public void ExecutableName_ReturnsMakeAppxExe()
    {
        Assert.AreEqual("makeappx.exe", new MakeAppxTool().ExecutableName);
    }

    [TestMethod]
    public void PrintErrorText_LogsStdoutFromFirstErrorLineOnly()
    {
        var logger = new CapturingLogger<MakeAppxTool>();
        var stdout = string.Join(Environment.NewLine,
            "Processing payload one",
            "MakeAppx : error: manifest invalid",
            "Details after error",
            "MakeAppx : error: package failed");

        new MakeAppxTool().PrintErrorText(stdout, stderr: string.Empty, logger);

        Assert.AreEqual(3, logger.Entries.Count);
        Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("Processing payload", StringComparison.Ordinal)));
        Assert.IsTrue(logger.Has(LogLevel.Error, "manifest invalid"));
        Assert.IsTrue(logger.Has(LogLevel.Error, "Details after error"));
        Assert.IsTrue(logger.Has(LogLevel.Error, "package failed"));
    }

    [TestMethod]
    public void PrintErrorText_LogsStderrWhenStdoutHasNoErrorLine()
    {
        var logger = new CapturingLogger<MakeAppxTool>();

        new MakeAppxTool().PrintErrorText("Processing payload", "stderr failure", logger);

        Assert.AreEqual(1, logger.Entries.Count);
        Assert.IsTrue(logger.Has(LogLevel.Error, "stderr failure"));
    }

    [TestMethod]
    public void PrintErrorText_FallsBackToBaseWhenNoStdoutErrorOrStderr()
    {
        var logger = new CapturingLogger<MakeAppxTool>();

        new MakeAppxTool().PrintErrorText("plain stdout", string.Empty, logger);

        Assert.IsTrue(logger.Entries.Count > 0, "Base Tool.PrintErrorText should surface available output.");
        Assert.IsTrue(logger.Entries.Any(e => e.Message.Contains("plain stdout", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PrintErrorText_TreatsWhitespaceStdoutAsAbsentAndUsesStderr()
    {
        var logger = new CapturingLogger<MakeAppxTool>();

        new MakeAppxTool().PrintErrorText("  ", "stderr only", logger);

        Assert.AreEqual(1, logger.Entries.Count);
        Assert.IsTrue(logger.Has(LogLevel.Error, "stderr only"));
    }
}


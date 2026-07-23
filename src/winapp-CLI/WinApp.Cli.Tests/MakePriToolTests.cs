// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class MakePriToolTests
{
    [TestMethod]
    public void ExecutableName_ReturnsMakePriExe()
    {
        Assert.AreEqual("makepri.exe", new MakePriTool().ExecutableName);
    }

    [TestMethod]
    public void PrintErrorText_LogsStderrWhenPresent()
    {
        var logger = new CapturingLogger<MakePriTool>();

        new MakePriTool().PrintErrorText("stdout details", "makepri stderr failure", logger);

        Assert.AreEqual(1, logger.Entries.Count);
        Assert.IsTrue(logger.Has(LogLevel.Error, "makepri stderr failure"));
        Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("stdout details", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PrintErrorText_FallsBackToBaseWhenStderrIsBlank()
    {
        var logger = new CapturingLogger<MakePriTool>();

        new MakePriTool().PrintErrorText("makepri stdout failure", " ", logger);

        Assert.IsTrue(logger.Entries.Count > 0, "Base Tool.PrintErrorText should surface stdout when makepri.exe produced no stderr.");
        Assert.IsTrue(logger.Entries.Any(e => e.Message.Contains("makepri stdout failure", StringComparison.Ordinal)));
    }
}


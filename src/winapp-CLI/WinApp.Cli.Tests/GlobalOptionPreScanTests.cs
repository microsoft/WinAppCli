// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class GlobalOptionPreScanTests
{
    [TestMethod]
    public void IsFlagPresent_LongName_ReturnsTrue()
    {
        Assert.IsTrue(GlobalOptionPreScan.IsFlagPresent(
            ["run", ".", "--json"], "--json", []));
    }

    [TestMethod]
    public void IsFlagPresent_Alias_ReturnsTrue()
    {
        Assert.IsTrue(GlobalOptionPreScan.IsFlagPresent(
            ["run", ".", "-v"], "--verbose", ["-v"]));
    }

    [TestMethod]
    public void IsFlagPresent_NotPresent_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.IsFlagPresent(
            ["run", "."], "--json", []));
    }

    [TestMethod]
    public void IsFlagPresent_FlagAfterDoubleDash_ReturnsFalse()
    {
        // Regression: `winapp run . -- --json` must NOT enable JSON mode for winapp.
        // The '--json' is a passthrough arg for the launched application.
        Assert.IsFalse(GlobalOptionPreScan.IsFlagPresent(
            ["run", ".", "--", "--json"], "--json", []));
    }

    [TestMethod]
    public void IsFlagPresent_FlagBeforeDoubleDash_ReturnsTrue()
    {
        // A real winapp global flag before '--' must still be recognised.
        Assert.IsTrue(GlobalOptionPreScan.IsFlagPresent(
            ["run", ".", "--json", "--", "--app-flag"], "--json", []));
    }

    [TestMethod]
    public void IsFlagPresent_EmptyArgs_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.IsFlagPresent(
            [], "--json", []));
    }

    [TestMethod]
    public void IsFlagPresent_OnlyDoubleDash_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.IsFlagPresent(
            ["--"], "--json", []));
    }
}

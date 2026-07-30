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

    // -------------------------------------------------------------------------
    // GetBooleanFlagValue — valid and invalid attached values
    // -------------------------------------------------------------------------

    [TestMethod]
    public void GetBooleanFlagValue_BareName_ReturnsTrue()
    {
        Assert.IsTrue(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_Absent_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_EqualsTrue_ReturnsTrue()
    {
        Assert.IsTrue(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json=true"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_EqualsTrueMixedCase_ReturnsTrue()
    {
        Assert.IsTrue(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json=True"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_EqualsFalse_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json=false"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_EqualsFalseMixedCase_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json=False"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_EqualsBogus_ReturnsFalse()
    {
        // M2 regression: an invalid attached value (not a bool) must NOT be coerced to true.
        // The real System.CommandLine parser will surface the parse error; the pre-scan must
        // return false so the spurious --json/--verbose conflict is not triggered.
        Assert.IsFalse(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json=bogus"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_SpaceTrue_ReturnsTrue()
    {
        Assert.IsTrue(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json", "true"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_SpaceFalse_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json", "false"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_SpaceNextOptionNotBool_ReturnsTrue()
    {
        // --json immediately followed by a non-bool token that looks like another option:
        // the bare --json is true; the next token is NOT consumed as the value.
        Assert.IsTrue(GlobalOptionPreScan.GetBooleanFlagValue(
            ["ui", "pen", "--json", "--verbose"], "--json", []));
    }

    [TestMethod]
    public void GetBooleanFlagValue_AfterDoubleDash_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.GetBooleanFlagValue(
            ["run", ".", "--", "--json"], "--json", []));
    }

    // -------------------------------------------------------------------------
    // TryFindInvalidBooleanValue — detect a non-boolean '='-attached value
    // -------------------------------------------------------------------------

    [TestMethod]
    public void TryFindInvalidBooleanValue_EqualsBogus_ReturnsTrueWithValue()
    {
        Assert.IsTrue(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen", "--json=bogus"], "--json", [], out var bad));
        Assert.AreEqual("bogus", bad);
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_EqualsTrue_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen", "--json=true"], "--json", [], out _));
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_EqualsFalseMixedCase_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen", "--json=False"], "--json", [], out _));
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_BareFlag_ReturnsFalse()
    {
        // The bare flag has no attached value, so it is not an invalid value.
        Assert.IsFalse(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen", "--json"], "--json", [], out _));
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_SpaceSeparated_ReturnsFalse()
    {
        // Space-separated forms are handled by the parser / GetBooleanFlagValue, not here.
        Assert.IsFalse(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen", "--json", "bogus"], "--json", [], out _));
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_EmptyValue_ReturnsTrue()
    {
        // "--json=" has an attached value that is not a valid boolean.
        Assert.IsTrue(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen", "--json="], "--json", [], out var bad));
        Assert.AreEqual(string.Empty, bad);
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_Alias_ReturnsTrue()
    {
        Assert.IsTrue(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen", "-v=bogus"], "--verbose", ["-v"], out var bad));
        Assert.AreEqual("bogus", bad);
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_AfterDoubleDash_ReturnsFalse()
    {
        // A '--json=bogus' passthrough after '--' must be ignored, not rejected.
        Assert.IsFalse(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["run", ".", "--", "--json=bogus"], "--json", [], out _));
    }

    [TestMethod]
    public void TryFindInvalidBooleanValue_Absent_ReturnsFalse()
    {
        Assert.IsFalse(GlobalOptionPreScan.TryFindInvalidBooleanValue(
            ["ui", "pen"], "--json", [], out _));
    }
}

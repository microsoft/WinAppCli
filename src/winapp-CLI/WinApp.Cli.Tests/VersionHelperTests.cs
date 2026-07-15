// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class VersionHelperTests
{
    [TestMethod]
    public void FormatVersion_InformationalVersion_ReturnedAsIs()
    {
        Assert.AreEqual("0.1.8", VersionHelper.FormatVersion("0.1.8", asmVersion: null));
    }

    [TestMethod]
    public void FormatVersion_InformationalVersionWithGitHash_StripsSuffix()
    {
        Assert.AreEqual("0.1.8", VersionHelper.FormatVersion("0.1.8+abc123def", asmVersion: null));
    }

    [TestMethod]
    public void FormatVersion_InformationalVersionEndingInPlus_StripsToEmptySuffix()
    {
        Assert.AreEqual("1.0.0", VersionHelper.FormatVersion("1.0.0+", asmVersion: null));
    }

    [TestMethod]
    public void FormatVersion_NullInformationalVersion_FallsBackToAssemblyVersion()
    {
        Assert.AreEqual("2.5.7", VersionHelper.FormatVersion(null, new Version(2, 5, 7, 99)));
    }

    [TestMethod]
    public void FormatVersion_EmptyInformationalVersion_FallsBackToAssemblyVersion()
    {
        Assert.AreEqual("3.4.0", VersionHelper.FormatVersion(string.Empty, new Version(3, 4, 0)));
    }

    [TestMethod]
    public void FormatVersion_NoInformationalAndNoAssemblyVersion_ReturnsZeroTriplet()
    {
        Assert.AreEqual("0.0.0", VersionHelper.FormatVersion(null, asmVersion: null));
    }

    [TestMethod]
    public void GetVersionString_ReturnsNonEmptyValue()
    {
        var version = VersionHelper.GetVersionString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(version), "The CLI version string must never be empty.");
    }
}

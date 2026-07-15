// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class SystemDefaultsHelperTests
{
    [TestMethod]
    public void BuildPublisherCN_RegularUser_WrapsWithCn()
    {
        Assert.AreEqual("CN=Alice", SystemDefaultsHelper.BuildPublisherCN("Alice"));
    }

    [TestMethod]
    public void BuildPublisherCN_NullUser_FallsBackToDeveloper()
    {
        Assert.AreEqual("CN=Developer", SystemDefaultsHelper.BuildPublisherCN(null));
    }

    [TestMethod]
    public void BuildPublisherCN_EmptyUser_FallsBackToDeveloper()
    {
        Assert.AreEqual("CN=Developer", SystemDefaultsHelper.BuildPublisherCN(string.Empty));
    }

    [TestMethod]
    public void BuildPublisherCN_WhitespaceUser_FallsBackToDeveloper()
    {
        Assert.AreEqual("CN=Developer", SystemDefaultsHelper.BuildPublisherCN("   "));
    }

    [TestMethod]
    public void GetDefaultPublisherCN_ReturnsCnPrefixedValue()
    {
        var cn = SystemDefaultsHelper.GetDefaultPublisherCN();
        Assert.IsTrue(cn.StartsWith("CN=", StringComparison.Ordinal), $"Expected a CN= prefix, got '{cn}'.");
    }

    [TestMethod]
    public void GetDefaultPackageName_NormalizesSpacesToHyphensAndLowercases()
    {
        var dir = new DirectoryInfo(@"C:\src\My Cool App");
        Assert.AreEqual("my-cool-app", SystemDefaultsHelper.GetDefaultPackageName(dir));
    }

    [TestMethod]
    public void GetDefaultDescription_ReturnsStableDefault()
    {
        Assert.AreEqual("My Application", SystemDefaultsHelper.GetDefaultDescription());
    }
}

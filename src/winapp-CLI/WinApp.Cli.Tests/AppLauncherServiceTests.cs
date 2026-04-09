// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AppLauncherServiceTests
{
    private readonly AppLauncherService _service = new(
        new Microsoft.Extensions.Logging.Abstractions.NullLogger<AppLauncherService>());

    // Known publisher → publisherId mappings obtained from Get-AppxPackage on Windows.
    // These are the ground truth values computed by the Windows platform.

    [TestMethod]
    [DataRow(
        "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        "cw5n1h2txyewy",
        DisplayName = "Microsoft Windows publisher")]
    [DataRow(
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        "8wekyb3d8bbwe",
        DisplayName = "Microsoft Corporation publisher")]
    [DataRow(
        "CN=CA0D5344-F590-41F9-BE2C-16BE6FCEE1DF",
        "rn9aeerfb38dg",
        DisplayName = "GUID-style publisher")]
    [DataRow(
        "CN=83564403-0B26-46B8-9D84-040F43691D31",
        "dt26b99r8h8gj",
        DisplayName = "GUID-style publisher 2")]
    [DataRow(
        "CN=Metulev",
        "j3adjyj8sqwmw",
        DisplayName = "Simple CN publisher")]
    public void ComputePackageFamilyName_MatchesWindowsValue(string publisher, string expectedPublisherId)
    {
        var pfn = _service.ComputePackageFamilyName("TestPackage", publisher);

        Assert.AreEqual($"TestPackage_{expectedPublisherId}", pfn);
    }

    [TestMethod]
    public void ComputePackageFamilyName_PublisherIsCaseSensitive()
    {
        // Windows treats publisher DN as case-sensitive for hash computation.
        // "CN=Test" and "cn=test" produce different publisher IDs.
        var pfn1 = _service.ComputePackageFamilyName("Pkg", "CN=Test");
        var pfn2 = _service.ComputePackageFamilyName("Pkg", "cn=test");

        Assert.AreNotEqual(pfn1, pfn2, "Publisher comparison should be case-sensitive");
    }

    [TestMethod]
    public void ComputePackageFamilyName_PublisherIdIs13Chars()
    {
        var pfn = _service.ComputePackageFamilyName("Pkg", "CN=AnyPublisher");

        // Format: {name}_{publisherId} where publisherId is exactly 13 chars
        var parts = pfn.Split('_');
        Assert.AreEqual(2, parts.Length, "PFN should have exactly one underscore");
        Assert.AreEqual(13, parts[1].Length, "Publisher ID should be exactly 13 characters");
    }

    [TestMethod]
    public void ComputePackageFamilyName_PublisherIdIsLowercase()
    {
        var pfn = _service.ComputePackageFamilyName("Pkg", "CN=SomePublisher");
        var publisherId = pfn.Split('_')[1];

        Assert.AreEqual(publisherId, publisherId.ToLowerInvariant(),
            "Publisher ID should be lowercase");
    }
}

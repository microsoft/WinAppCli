// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the <c>sandbox:</c> prefix, which selects a side in <c>cp</c> and a routing target in
/// <c>ui</c>.
/// </summary>
/// <remarks>
/// The rule that matters is that exactly one endpoint may carry it. Inferring the direction from
/// which path happens to exist would let the command guess wrong about which side is being
/// overwritten — and it would guess differently depending on the state of the machine.
/// </remarks>
[TestClass]
public class SandboxCopyParserTests
{
    [TestMethod]
    public void Parse_HostToGuest()
    {
        var request = SandboxCopyParser.Parse(@".\setup.ps1", @"sandbox:C:\Setup\setup.ps1");

        Assert.AreEqual(SandboxCopyDirection.ToGuest, request.Direction);
        Assert.AreEqual(@"C:\Setup\setup.ps1", request.GuestPath);
        Assert.IsTrue(Path.IsPathFullyQualified(request.HostPath));
    }

    [TestMethod]
    public void Parse_GuestToHost()
    {
        var request = SandboxCopyParser.Parse(@"sandbox:C:\Results", @".\results");

        Assert.AreEqual(SandboxCopyDirection.FromGuest, request.Direction);
        Assert.AreEqual(@"C:\Results", request.GuestPath);
        Assert.IsTrue(Path.IsPathFullyQualified(request.HostPath));
    }

    [TestMethod]
    public void Parse_NeitherSideIsGuest_IsRefused()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => SandboxCopyParser.Parse(@".\a", @".\b"));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
        Assert.IsNotNull(failure.Error.Example);
    }

    [TestMethod]
    public void Parse_BothSidesAreGuest_IsRefused()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => SandboxCopyParser.Parse(@"sandbox:C:\a", @"sandbox:C:\b"));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    [TestMethod]
    public void Parse_EmptyGuestPath_IsRefused()
    {
        Assert.ThrowsExactly<ExecutionTargetException>(
            () => SandboxCopyParser.Parse(@".\a", "sandbox:"));
    }

    [TestMethod]
    public void Parse_PrefixIsCaseInsensitive()
    {
        var request = SandboxCopyParser.Parse(@".\a", @"Sandbox:C:\b");
        Assert.AreEqual(SandboxCopyDirection.ToGuest, request.Direction);
    }

    [TestMethod]
    public void AppTarget_PrefixIsRemovedBeforeTheGuestSeesIt()
    {
        // The prefix selects where the command runs; the guest then resolves an ordinary target, so
        // its own resolution logic is unchanged.
        Assert.IsTrue(SandboxAppTarget.IsRouted("sandbox:MyApp"));
        Assert.AreEqual("MyApp", SandboxAppTarget.Unwrap("sandbox:MyApp"));

        Assert.IsTrue(SandboxAppTarget.IsRouted("sandbox:4212"));
        Assert.AreEqual("4212", SandboxAppTarget.Unwrap("sandbox:4212"));
    }

    [TestMethod]
    public void AppTarget_PlainValuesAreUntouched()
    {
        Assert.IsFalse(SandboxAppTarget.IsRouted("MyApp"));
        Assert.AreEqual("MyApp", SandboxAppTarget.Unwrap("MyApp"));
        Assert.IsFalse(SandboxAppTarget.IsRouted(null));
        Assert.IsNull(SandboxAppTarget.Unwrap(null));
    }

    [TestMethod]
    public void GuestPathsAreRelativeToTheManagedWorkRoot()
    {
        // Relative paths are the contract: the guest resolves them against a root it owns, which is
        // what makes containment provable.
        Assert.AreEqual(@"Work\build", SandboxCopyService.NormalizeGuestRelative(@"Work\build"));
        Assert.AreEqual(@"Work\build", SandboxCopyService.NormalizeGuestRelative(@"Work/build"));
        Assert.AreEqual(@"Setup\setup.ps1", SandboxCopyService.NormalizeGuestRelative(@"Setup\setup.ps1"));

        // Spaces and non-ASCII names survive untouched; only the root is at issue.
        Assert.AreEqual(@"My Tools\ünïcode.ps1", SandboxCopyService.NormalizeGuestRelative(@"My Tools/ünïcode.ps1"));
    }

    /// <summary>
    /// A rooted guest path is refused rather than silently re-rooted.
    /// </summary>
    /// <remarks>
    /// Stripping the drive and continuing looks helpful and is not: the file lands somewhere the
    /// caller never named, the copy reports success, and the command they run next — using the path
    /// they actually typed — cannot find it.
    /// </remarks>
    [TestMethod]
    [DataRow(@"C:\Setup\setup.ps1", DisplayName = "drive-absolute")]
    [DataRow(@"\Setup\setup.ps1", DisplayName = "rooted")]
    [DataRow(@"/Setup/setup.ps1", DisplayName = "rooted, forward slashes")]
    [DataRow(@"\\server\share\setup.ps1", DisplayName = "UNC")]
    [DataRow(@"C:\", DisplayName = "bare drive")]
    public void RootedGuestPaths_AreRefusedWithTheWorkRootNamed(string guestPath)
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => SandboxCopyService.NormalizeGuestRelative(guestPath));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);

        // The message has to name where paths actually resolve, or the user cannot act on it.
        StringAssert.Contains(failure.Error.Message, SandboxCopyService.GuestWorkRoot);
    }

    /// <summary>Traversal out of the managed folder is refused.</summary>
    [TestMethod]
    [DataRow(@"..\escape.ps1")]
    [DataRow(@"Setup\..\..\escape.ps1")]
    [DataRow(@"Setup/../../escape.ps1")]
    public void TraversingGuestPaths_AreRefused(string guestPath)
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => SandboxCopyService.NormalizeGuestRelative(guestPath));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    /// <summary>The reported destination is the path a following command should use.</summary>
    [TestMethod]
    public void ResolvedGuestPath_IsReportedFullyQualified()
    {
        Assert.AreEqual(
            $@"{SandboxCopyService.GuestWorkRoot}\Setup\setup.ps1",
            SandboxCopyService.DescribeGuestPath(@"Setup\setup.ps1"));

        Assert.AreEqual(
            SandboxCopyService.GuestWorkRoot,
            SandboxCopyService.DescribeGuestPath(string.Empty));
    }

    [TestMethod]
    public void ResolveHostDestination_RejectsGuestTraversalOutsideTheRequestedDirectory()
    {
        var destination = TestPaths.TempRoot("sandbox-copy-out");
        Directory.CreateDirectory(destination);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            SandboxCopyService.ResolveHostDestination(
                destination,
                @"Work\results",
                @"Work\results\..\..\..\startup.cmd",
                matchCount: 2));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }
}

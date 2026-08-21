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
    public void GuestPathsAreReducedToRelativeFormForContainment()
    {
        // Users write guest paths the way they think of them, including drive letters. Reducing
        // them here is what lets the guest resolve them against a managed root and prove
        // containment, instead of a guest-provided path selecting an arbitrary location.
        Assert.AreEqual(@"Work\build", SandboxCopyService.NormalizeGuestRelative(@"C:\Work\build"));
        Assert.AreEqual(@"Work\build", SandboxCopyService.NormalizeGuestRelative(@"\Work\build"));
        Assert.AreEqual(@"Work\build", SandboxCopyService.NormalizeGuestRelative(@"Work/build"));
        Assert.AreEqual(string.Empty, SandboxCopyService.NormalizeGuestRelative(@"C:\"));
    }
}

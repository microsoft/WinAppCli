// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// <c>unregister --sandbox</c> must fail with structured, state-repair guidance when a
/// deployment's registration layout is missing its manifest, rather than handing guest winapp's
/// <c>--manifest</c> option a path that fails argument parsing and prints usage help.
/// </summary>
[TestClass]
public class UnregisterCommandSandboxTests
{
    [TestMethod]
    public void EnsureLayoutHasManifest_WhenTheManifestIsPresent_DoesNothing()
    {
        var files = new List<GuestFileInfo>
        {
            new("appxmanifest.xml", 10, 0, "hash"),
            new("app.exe", 20, 0, "hash2"),
        };

        // Must not throw.
        UnregisterCommand.Handler.EnsureLayoutHasManifest(files, "dep-1");
    }

    [TestMethod]
    public void EnsureLayoutHasManifest_IsCaseInsensitive()
    {
        var files = new List<GuestFileInfo> { new("AppXManifest.XML", 10, 0, "hash") };

        UnregisterCommand.Handler.EnsureLayoutHasManifest(files, "dep-1");
    }

    [TestMethod]
    public void EnsureLayoutHasManifest_WhenTheManifestIsMissing_FailsWithStateRepairGuidance()
    {
        var files = new List<GuestFileInfo> { new("app.exe", 20, 0, "hash2") };

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => UnregisterCommand.Handler.EnsureLayoutHasManifest(files, "dep-1"));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        Assert.IsNotNull(failure.Error.UserAction);
        Assert.IsNotNull(failure.Error.Example);
        Assert.AreEqual("dep-1", failure.Error.Context!["deploymentId"]);
    }

    [TestMethod]
    public void EnsureLayoutHasManifest_WhenTheLayoutIsEmpty_FailsWithStateRepairGuidance()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => UnregisterCommand.Handler.EnsureLayoutHasManifest([], "dep-1"));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
    }
}

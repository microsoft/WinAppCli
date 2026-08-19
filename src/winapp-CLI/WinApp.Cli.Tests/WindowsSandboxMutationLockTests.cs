// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Principal;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowsSandboxMutationLockTests
{
    [TestMethod]
    public void DefaultName_IsGlobalAndScopedToCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User;
        Assert.IsNotNull(sid);

        var mutationLock = new WindowsSandboxMutationLock();

        Assert.AreEqual(WindowsSandboxMutationLock.DefaultNamePrefix + sid.Value, mutationLock.Name);
    }

    [TestMethod]
    public void Acquire_WaitsForCurrentOwnerAndHonorsCancellation()
    {
        var mutexName = @"Local\WinApp.Cli.Tests." + Guid.NewGuid().ToString("N");
        var mutationLock = new WindowsSandboxMutationLock(mutexName);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var first = mutationLock.Acquire();
            acquired.Set();
            release.Wait();
        });
        owner.Start();
        Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            Assert.ThrowsExactly<OperationCanceledException>(
                () => mutationLock.Acquire(cts.Token));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(owner.Join(TimeSpan.FromSeconds(5)));
        }
    }
}

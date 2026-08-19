// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets;

namespace WinApp.Cli.Tests;

[TestClass]
public class ExecutionTargetServiceTests
{
    [TestMethod]
    public void ExecutionTargetRef_RejectsInvalidValues()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ExecutionTargetRef("Windows-Sandbox", "Windows-Sandbox:default"));
        Assert.ThrowsExactly<ArgumentException>(() => new ExecutionTargetRef("windows-sandbox", "other:default"));
        Assert.ThrowsExactly<ArgumentException>(() => new ExecutionTargetRef("windows-sandbox", "windows-sandbox:bad/name"));
    }

    [TestMethod]
    public void ExecutionTargetEpoch_RequiresCanonicalOpaqueValue()
    {
        var epoch = new ExecutionTargetEpoch("ABCDEF0123456789ABCDEF0123456789");

        Assert.AreEqual("abcdef0123456789abcdef0123456789", epoch.Value);
        Assert.ThrowsExactly<ArgumentException>(() => new ExecutionTargetEpoch("not-an-epoch"));
    }

    [TestMethod]
    public async Task EnsureAsync_DelegatesToMatchingBackend()
    {
        var backend = new FakeBackend();
        var service = new ExecutionTargetService(backend);

        var result = await service.EnsureAsync(
            ExecutionTargetRef.WindowsSandboxDefault,
            new ExecutionTargetRequirements());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, backend.EnsureCount);
        Assert.AreEqual(ExecutionTargetRef.WindowsSandboxDefault, result.Instance!.Target);
    }

    [TestMethod]
    public async Task EnsureAsync_RejectsBackendMismatchWithoutCallingBackend()
    {
        var backend = new FakeBackend();
        var service = new ExecutionTargetService(backend);

        var result = await service.EnsureAsync(
            new ExecutionTargetRef("other", "other:default"),
            new ExecutionTargetRequirements());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, backend.EnsureCount);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.BackendMismatch, result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task EnsureAsync_RejectsCapabilitiesNotSatisfiedByBackend()
    {
        var backend = new FakeBackend
        {
            Capabilities = new ExecutionTargetCapabilities(true, false, false, false, false, false),
        };
        var service = new ExecutionTargetService(backend);

        var result = await service.EnsureAsync(
            ExecutionTargetRef.WindowsSandboxDefault,
            new ExecutionTargetRequirements(InteractiveDesktop: true));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExecutionTargetDiagnosticCode.CapabilityUnavailable, result.Diagnostics.Single().Code);
    }

    private sealed class FakeBackend : IExecutionTargetBackend
    {
        public ExecutionTargetRef Target => ExecutionTargetRef.WindowsSandboxDefault;

        public ExecutionTargetCapabilities Capabilities { get; set; } =
            new(true, false, false, false, false, false);

        public int EnsureCount { get; private set; }

        public Task<ExecutionTargetProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ExecutionTargetProbeResult.Supported);

        public Task<ExecutionTargetStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionTargetStatusResult(
                Target,
                ExecutionTargetStatus.Running,
                "sandbox-id",
                new ExecutionTargetEpoch("0123456789abcdef0123456789abcdef"),
                Capabilities,
                []));

        public Task<ExecutionTargetEnsureResult> EnsureAsync(
            ExecutionTargetRequirements requirements,
            CancellationToken cancellationToken = default)
        {
            EnsureCount++;
            return Task.FromResult(new ExecutionTargetEnsureResult(
                new ExecutionTargetInstance(
                    Target,
                    "sandbox-id",
                    new ExecutionTargetEpoch("0123456789abcdef0123456789abcdef"),
                    Capabilities),
                []));
        }
    }
}

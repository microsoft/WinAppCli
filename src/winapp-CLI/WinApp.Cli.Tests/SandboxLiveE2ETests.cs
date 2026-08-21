// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Live Windows Sandbox coverage, gated on <c>WINAPP_SANDBOX_E2E=1</c>.
/// </summary>
/// <remarks>
/// Everything else in the execution-target suite runs the real host channel against the real guest
/// server over an in-memory transport, which is what proves orchestration does not depend on Windows
/// Sandbox. These tests prove the opposite half: that the backend's assumptions about <c>wsb.exe</c>,
/// the guest session, and the agent bootstrap actually hold on a real machine.
/// <para>
/// They are gated because Windows permits exactly one Sandbox at a time and creating one is a
/// machine-wide, visible side effect. Running them by default would take that resource away from
/// whoever is using the machine, and would fail on any host without the optional feature.
/// </para>
/// <para>
/// <b>These tests never stop a Sandbox winapp did not create.</b> If an unowned instance is running,
/// they report it and stop, exactly as the product does. Cleanup only ever touches what the test
/// itself created, and always runs.
/// </para>
/// </remarks>
[TestClass]
public class SandboxLiveE2ETests
{
    /// <summary>Set to <c>1</c> to run these tests.</summary>
    internal const string GateVariable = "WINAPP_SANDBOX_E2E";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void RequireGate()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {GateVariable}=1 on a machine with Windows Sandbox to run live execution-target coverage.");
        }
    }

    /// <summary>
    /// Support probing must be honest about this machine before anything is built or created.
    /// </summary>
    [TestMethod]
    public async Task Support_IsProbedWithoutCreatingAnything()
    {
        var backend = CreateBackend();
        var support = await backend.ProbeSupportAsync(TestContext.CancellationToken);

        if (!support.IsSupported)
        {
            Assert.Inconclusive(
                $"This machine cannot run Windows Sandbox: {support.Error?.Code} — {support.Error?.Message}");
        }

        // Probing must not have created an instance. Anything running now was not ours.
        Assert.IsTrue(support.IsSupported);
    }

    /// <summary>
    /// A cold start, a warm reuse, and a round trip through the real transport.
    /// </summary>
    /// <remarks>
    /// Deliberately one test rather than three. Each stage depends on the previous one having left a
    /// live Sandbox, and splitting them would either serialize on shared machine state between test
    /// methods or create and tear down a Sandbox three times.
    /// </remarks>
    [TestMethod]
    public async Task ColdStartThenWarmReuse_RunsACommandInTheGuest()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        var orchestrator = new ExecutionTargetOrchestrator(CreateBackend(), new TargetMutationLock(new TargetStateDirectoryProvider()));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        try
        {
            string? firstEpoch;

            await using (var cold = await orchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating, timeout.Token))
            {
                Assert.IsFalse(cold.Reused, "The first prepare on an empty machine is a cold start.");
                Assert.IsNotNull(cold.Capabilities.ManagedRoot, "The guest must report where it stores deployments.");
                Assert.IsTrue(cold.Capabilities.SupportsInteractiveDesktop);

                firstEpoch = cold.Epoch.Value;

                var result = await cold.Channel.ExecuteAsync(
                    new GuestExecRequest
                    {
                        Executable = "cmd.exe",
                        Arguments = ["/c", "exit", "7"],
                    },
                    callbacks: null,
                    timeout.Token);

                // The guest application's own exit code, distinct from an infrastructure failure.
                Assert.AreEqual(7, result.ExitCode);
            }

            await using var warm = await orchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating, timeout.Token);

            Assert.IsTrue(warm.Reused, "The second prepare must reuse the instance rather than recreate it.");
            Assert.AreEqual(firstEpoch, warm.Epoch.Value, "Reuse must stay in the same generation.");
        }
        finally
        {
            await StopOwnedSandboxAsync();
        }
    }

    /// <summary>
    /// A file placed in the guest comes back byte-identical.
    /// </summary>
    [TestMethod]
    public async Task FilesRoundTripThroughTheGuest()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        var orchestrator = new ExecutionTargetOrchestrator(CreateBackend(), new TargetMutationLock(new TargetStateDirectoryProvider()));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var root = TestPaths.TempRoot(nameof(FilesRoundTripThroughTheGuest));
        var source = TestPaths.Under(root, "source.bin");
        var returned = TestPaths.Under(root, "returned.bin");

        Directory.CreateDirectory(root);
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(source, content, timeout.Token);

        try
        {
            await using var target = await orchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false }, timeout.Token);

            var scope = new GuestPathScope(GuestRootNames.Work, Scope: null);

            await using (var stream = File.OpenRead(source))
            {
                await target.Channel.PutFileAsync(
                    scope,
                    new GuestFileInfo("roundtrip.bin", content.Length, DateTime.UtcNow.Ticks, Sha256(content)),
                    stream,
                    timeout.Token);
            }

            await using (var stream = File.Create(returned))
            {
                await target.Channel.GetFileAsync(scope, "roundtrip.bin", stream, timeout.Token);
            }

            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(returned, timeout.Token));

            await target.Channel.DeleteFilesAsync(scope, ["roundtrip.bin"], timeout.Token);
        }
        finally
        {
            TryDeleteDirectory(root);
            await StopOwnedSandboxAsync();
        }
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static WindowsSandboxCli CreateCli() => new(new ProcessRunner());

    private static WindowsSandboxBackend CreateBackend()
    {
        var cli = CreateCli();
        var provider = new TargetStateDirectoryProvider();

        return new WindowsSandboxBackend(cli, new WindowsSandboxLifecycle(cli, new TargetStateStore(provider)), provider);
    }

    /// <summary>
    /// Skips rather than fails when the machine cannot run Sandbox, or when someone else's is up.
    /// </summary>
    /// <remarks>
    /// An unowned instance is not a test failure and must never be stopped: it may hold work that
    /// matters to whoever created it. Windows allows only one, so the honest outcome is to say so
    /// and stop.
    /// </remarks>
    private async Task SkipIfUnsupportedOrOccupiedAsync()
    {
        var support = await CreateBackend().ProbeSupportAsync(TestContext.CancellationToken);

        if (!support.IsSupported)
        {
            Assert.Inconclusive(
                $"This machine cannot run Windows Sandbox: {support.Error?.Code} — {support.Error?.Message}");
        }

        var running = await CreateCli().ListAsync(TestContext.CancellationToken);
        var owned = new TargetStateStore(new TargetStateDirectoryProvider())
            .Read(ExecutionTargetRef.WindowsSandboxDefault)?.InstanceId;

        if (running.Any(id => !string.Equals(id, owned, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Inconclusive(
                "A Windows Sandbox instance that winapp does not own is already running. " +
                "Windows permits only one, and this test will not stop an instance it did not create.");
        }
    }

    /// <summary>
    /// Stops only the instance recorded as winapp's own, so the machine is left as it was found.
    /// </summary>
    private static async Task StopOwnedSandboxAsync()
    {
        try
        {
            var state = new TargetStateStore(new TargetStateDirectoryProvider())
                .Read(ExecutionTargetRef.WindowsSandboxDefault);

            if (state?.InstanceId is not { Length: > 0 } instanceId)
            {
                return;
            }

            await CreateCli().StopAsync(instanceId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ExecutionTargetException or IOException or UnauthorizedAccessException)
        {
            // Cleanup failure must not mask the assertion that already ran.
            Trace.TraceWarning("Could not stop the Sandbox this test created: {0}", ex.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not remove '{0}': {1}", path, ex.Message);
        }
    }
}

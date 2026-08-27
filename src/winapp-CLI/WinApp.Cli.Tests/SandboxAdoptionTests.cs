// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for what happens to a Windows Sandbox winapp did not start.
/// </summary>
/// <remarks>
/// <c>--sandbox</c> takes one over rather than refusing it, so these pin the two halves of that
/// decision: the guest really is prepared — which is a mutation, not a read — and the instance is
/// never stopped, on any path, including every failure path.
/// </remarks>
[TestClass]
public class SandboxAdoptionTests
{
    /// <summary>The instance a user started themselves, before winapp ran at all.</summary>
    private const string ManualInstanceId = "manually-started-sandbox";

    [TestMethod]
    public async Task AdoptedInstance_ThatAlreadyHasAClient_IsNotGivenASecondOne()
    {
        // Regression, measured on a live Sandbox: `wsb connect` against an instance whose client is
        // already attached starts a SECOND WindowsSandboxRemoteSession rather than reusing the
        // first, and that extra client is still running after `wsb stop`. Taking over a Sandbox the
        // user already has open is exactly the case that hits it.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Ready;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.StartsWith("probe-session", StringComparison.Ordinal)),
            "Whether to connect must be decided by asking the guest.");
        Assert.IsFalse(
            harness.Cli.Operations.Any(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "A guest that already has an interactive session must not be handed another client.");
    }

    [TestMethod]
    public async Task AdoptedInstance_WithNoLoginSession_IsConnectedExactlyOnce()
    {
        // A Sandbox started headless by `wsb start` has no session until a client attaches, which is
        // what makes this the case that genuinely needs one.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.NoLoginSession;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            1,
            harness.Cli.Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "Exactly one client, for a guest that had none.");
    }

    [TestMethod]
    public async Task AdoptedInstance_WhoseSessionCannotBeDetermined_IsConnected()
    {
        // Without a session nothing else works, so an unanswerable probe errs toward making the
        // guest usable rather than toward leaving it unreachable.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Unknown;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            1,
            harness.Cli.Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AdoptedInstance_IsPreparedLikeAFreshGuest()
    {
        // A guest winapp did not start has none of winapp's setup in it, whatever else is running
        // there. Treating it as warm would connect to an agent that does not exist.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.StartsWith($"connect:{ManualInstanceId}", StringComparison.Ordinal)),
            "An adopted guest needs a connected client to have an interactive session.");
        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.Contains("AllowDevelopmentWithoutDevLicense", StringComparison.Ordinal)),
            "Developer Mode is machine-wide and nothing has set it in a guest winapp did not start.");
        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal)),
            "The agent's inbound rule must be created in an adopted guest too.");
    }

    [TestMethod]
    public async Task AdoptedInstance_IsNeverStopped_OnAnyFailurePath()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            harness.Cli.Stopped,
            "A Sandbox winapp did not start must survive whatever winapp fails at.");
    }

    [TestMethod]
    public async Task AdoptedInstance_IsReportedAsAdoptedInDiagnostics()
    {
        // A failure envelope has to say plainly that the Sandbox in play is one winapp took over, so
        // a reader knows why it is still running afterwards.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var diagnostics = harness.Backend.DescribeForDiagnostics();

        Assert.AreEqual(ManualInstanceId, diagnostics["sandboxId"]);
        Assert.AreEqual("true", diagnostics["sandboxAdopted"]);
    }

    [TestMethod]
    public async Task AdoptedInstance_GetsItsOwnBootstrapPathsRatherThanFixedOnes()
    {
        // A guest that has been managed before may already have a folder mapped at a fixed name, and
        // an agent still running out of it. A per-generation name is what makes "the folder winapp
        // just mapped" unambiguous.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var shares = GuestSharePaths(harness);

        Assert.AreEqual(2, shares.Count, "Exactly the read-only bootstrap and the writable result folder.");

        foreach (var share in shares)
        {
            Assert.AreNotEqual(@"C:\WinAppBootstrap", share, "Fixed guest paths can collide with a previous manager.");
            Assert.AreNotEqual(@"C:\WinAppBootstrapResult", share);
            StringAssert.StartsWith(share, @"C:\WinAppBootstrap", StringComparison.Ordinal);
        }

        Assert.AreEqual(2, shares.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public async Task TwoGenerations_DoNotShareABootstrapPath()
    {
        using var first = new AdoptionHarness();
        first.Cli.SetRunning("sandbox-one");
        await first.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        using var second = new AdoptionHarness();
        second.Cli.SetRunning("sandbox-two");
        await second.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var firstPaths = GuestSharePaths(first);
        var secondPaths = GuestSharePaths(second);

        CollectionAssert.AreNotEqual(
            firstPaths,
            secondPaths,
            "Each generation must map its own guest paths, so a stale mapping cannot be mistaken for the current one.");
    }

    [TestMethod]
    public async Task AgentLaunchCommand_PointsAtThisGenerationsFolders()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var launch = harness.Cli.Operations.Single(op => op.StartsWith("launch-agent:", StringComparison.Ordinal));
        var shares = GuestSharePaths(harness);

        foreach (var share in shares)
        {
            StringAssert.Contains(
                launch,
                share,
                StringComparison.Ordinal,
                "The agent must be told the folders this generation actually mapped.");
        }
    }

    [TestMethod]
    public async Task FirewallRule_NamesThisGenerationsAgentPath()
    {
        // The rule is scoped to the agent program. Naming a stale path would authorise nothing, or
        // worse, authorise a binary from a previous generation.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var rule = harness.Cli.Operations.Single(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal));
        var bootstrapShare = GuestSharePaths(harness)
            .Single(path => !path.StartsWith(@"C:\WinAppBootstrapResult", StringComparison.Ordinal));

        StringAssert.Contains(rule, $@"{bootstrapShare}\{GuestAgentInstaller.BinaryName}", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task FirewallCleanup_CannotRemoveAnotherManagersRule()
    {
        // The rule cleanup removes existing rules for the agent program path. When that path was a
        // fixed name, a second winapp -- one whose state root was redirected, so it could not see
        // this one's ownership record -- would delete the live agent's rule and cut its connection.
        // Scoping the path to this generation is what makes that impossible.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var rule = harness.Cli.Operations.Single(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal));

        StringAssert.Contains(
            rule,
            ".Program -eq $agent",
            StringComparison.Ordinal,
            "Removal must be scoped to this generation's agent path, not to every winapp agent.");
        Assert.IsFalse(
            rule.Contains(@"'C:\WinAppBootstrap\", StringComparison.Ordinal),
            "A fixed agent path would match another generation's rule.");
    }

    [TestMethod]
    public async Task ManyGenerations_ArePrunedWithoutTouchingTheCurrentOne()
    {
        // Regression: `bootstrap-result-<token>` also starts with `bootstrap-`, so a single prefix
        // match counted result folders toward the read-only family's retention -- and could delete
        // the result folder this very run had just mapped into the guest.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        var stateRoot = harness.TargetRoot;
        Directory.CreateDirectory(stateRoot);

        for (var generation = 0; generation < 12; generation++)
        {
            Directory.CreateDirectory(Path.Join(stateRoot, $"bootstrap-old{generation:00}0000000000000"));
            Directory.CreateDirectory(Path.Join(stateRoot, $"bootstrap-result-old{generation:00}0000000000000"));
        }

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var shares = GuestSharePaths(harness);
        var remaining = new DirectoryInfo(stateRoot).GetDirectories();

        foreach (var share in shares)
        {
            var name = share[@"C:\WinApp".Length..];
            var hostName = name.StartsWith("BootstrapResult-", StringComparison.Ordinal)
                ? "bootstrap-result-" + name["BootstrapResult-".Length..]
                : "bootstrap-" + name["Bootstrap-".Length..];

            Assert.IsTrue(
                remaining.Any(directory => string.Equals(directory.Name, hostName, StringComparison.OrdinalIgnoreCase)),
                $"The folder mapped into the guest as '{share}' must survive pruning.");
        }

        var bootstraps = remaining.Count(d =>
            d.Name.StartsWith("bootstrap-", StringComparison.Ordinal) &&
            !d.Name.StartsWith("bootstrap-result-", StringComparison.Ordinal));
        var results = remaining.Count(d => d.Name.StartsWith("bootstrap-result-", StringComparison.Ordinal));

        var ceiling = WindowsSandboxBackend.MaxRetainedBootstrapGenerations + 1;

        Assert.IsLessThanOrEqualTo(ceiling, bootstraps, "Stale read-only generations must be bounded.");
        Assert.IsLessThanOrEqualTo(ceiling, results, "Stale result generations must be bounded.");
    }

    private static List<string> GuestSharePaths(AdoptionHarness harness) =>
        [.. harness.Cli.Operations
            .Where(op => op.StartsWith("share|", StringComparison.Ordinal))
            .Select(op => op.Split('|')[1])
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Drives the real backend against a Sandbox it did not start, stopping at the agent launch.
    /// </summary>
    /// <remarks>
    /// The agent launch is the last step before the host would open a TCP connection to a guest that
    /// does not exist here, so the fake CLI throws a sentinel at exactly that point. Everything
    /// before it — take-over, sharing, connecting, Developer Mode, the firewall rule — is the real
    /// code path.
    /// </remarks>
    private sealed class AdoptionHarness : IDisposable
    {
        private readonly DirectoryInfo _root;

        public AdoptionHarness()
        {
            _root = new DirectoryInfo(TestPaths.TempRoot(nameof(SandboxAdoptionTests)));
            _root.Create();

            var directories = new TargetStateDirectoryProvider(_root.FullName);
            var stateStore = new TargetStateStore(directories);
            Cli = new AdoptionSandboxCli();

            TargetRoot = directories
                .GetTargetRoot(ExecutionTargetRef.WindowsSandboxDefault, create: true)
                .FullName;

            var binary = new FileInfo(Path.Join(_root.FullName, "winapp.exe"));
            File.WriteAllText(binary.FullName, "agent");

            Backend = new WindowsSandboxBackend(
                Cli,
                new WindowsSandboxLifecycle(Cli, stateStore),
                directories,
                new StaticBinaryProvider(binary),
                new NoOpWindowController(),
                setup: null,
                stateStore,
                NullTargetProgress.Instance);
        }

        public AdoptionSandboxCli Cli { get; }

        public WindowsSandboxBackend Backend { get; }

        /// <summary>Where this target's per-generation bootstrap folders live.</summary>
        public string TargetRoot { get; }

        public async Task RunUntilAgentLaunchAsync(CancellationToken cancellationToken)
        {
            await Assert.ThrowsExactlyAsync<AgentLaunchReached>(
                () => Backend.EnsureConnectedAsync(new EnsureTargetOptions(true), cancellationToken));
        }

        public void Dispose()
        {
            try
            {
                _root.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp cleanup is not worth failing a test over.
            }
        }
    }

    /// <summary>Marks the point the harness stops, rather than a failure under test.</summary>
    private sealed class AgentLaunchReached : Exception;

    /// <summary>A Sandbox CLI that records everything, including anything it was asked to stop.</summary>
    private sealed class AdoptionSandboxCli : IWindowsSandboxCli
    {
        private readonly List<string> _running = [];

        public bool IsAvailable => true;

        public List<string> Operations { get; } = [];

        public List<string> Stopped { get; } = [];

        public void UseExecutable(string executablePath)
        {
        }

        public void SetRunning(params string[] ids)
        {
            _running.Clear();
            _running.AddRange(ids);
        }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. _running]);

        public Task<string> StartAsync(string instanceId, string? configuration, CancellationToken cancellationToken)
        {
            Operations.Add($"start:{instanceId}");
            _running.Add(instanceId);
            return Task.FromResult(instanceId);
        }

        public Task StopAsync(string id, CancellationToken cancellationToken)
        {
            Stopped.Add(id);
            _running.Remove(id);
            return Task.CompletedTask;
        }

        public Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_running.Contains(id, StringComparer.OrdinalIgnoreCase));

        public Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult("172.27.0.2");

        /// <summary>What the guest reports when asked whether it already has a login session.</summary>
        public GuestSessionAvailability Session { get; set; } = GuestSessionAvailability.NoLoginSession;

        public Task<GuestSessionAvailability> ProbeInteractiveSessionAsync(
            string id,
            CancellationToken cancellationToken)
        {
            Operations.Add($"probe-session:{id}");
            return Task.FromResult(Session);
        }

        public Task ShareFolderAsync(
            string id,
            string hostPath,
            string sandboxPath,
            bool allowWrite,
            CancellationToken cancellationToken)
        {
            Operations.Add($"share|{sandboxPath}|{allowWrite}");
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string id, CancellationToken cancellationToken)
        {
            Operations.Add($"connect:{id}");
            return Task.CompletedTask;
        }

        public Task<int> ExecuteAsync(
            string id,
            string command,
            string? workingDirectory,
            bool asSystem,
            CancellationToken cancellationToken)
        {
            Operations.Add(command);
            return Task.FromResult(0);
        }

        public Task LaunchAgentAsync(string id, string command, CancellationToken cancellationToken)
        {
            Operations.Add($"launch-agent:{command}");

            // Everything after this point needs a guest that does not exist in a unit test.
            throw new AgentLaunchReached();
        }
    }

    private sealed class StaticBinaryProvider(FileInfo binary) : IHostWinappBinaryProvider
    {
        public FileInfo GetBinary() => binary;
    }

    private sealed class NoOpWindowController : IWindowsSandboxWindowController
    {
        public WindowsSandboxWindowSnapshot Capture() => new(new HashSet<int>(), default);

        public Task PlaceConnectedClientAsync(
            WindowsSandboxWindowSnapshot snapshot,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

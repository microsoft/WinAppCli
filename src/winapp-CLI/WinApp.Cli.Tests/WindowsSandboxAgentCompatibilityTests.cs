// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Verifies that a later host process only reuses a warm Sandbox agent whose persisted identity makes
/// that safe.
/// </summary>
[TestClass]
public class WindowsSandboxAgentCompatibilityTests
{
    private const string InstanceId = "sandbox-existing";
    private const string BootNonce = "nonce-existing";
    private const string Loopback = "127.0.0.1";

    private DirectoryInfo _root = null!;
    private TargetStateDirectoryProvider _directories = null!;
    private TargetStateStore _stateStore = null!;
    private CompatibilitySandboxCli _cli = null!;
    private FileInfo _hostBinary = null!;

    public TestContext TestContext { get; set; } = null!;

    private static ExecutionTargetRef Target => WindowsSandboxTarget.Default;

    private static ExecutionTargetEpoch Epoch =>
        ExecutionTargetEpoch.Create(InstanceId, BootNonce);

    private ExecutionTargetEpoch CurrentEpoch
    {
        get
        {
            var state = _stateStore.Read(Target);
            Assert.IsNotNull(state);
            Assert.IsFalse(string.IsNullOrWhiteSpace(state.InstanceId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(state.BootNonce));
            return ExecutionTargetEpoch.Create(state.InstanceId!, state.BootNonce!);
        }
    }

    private string TargetRoot =>
        _directories.GetTargetRoot(Target, create: true).FullName;

    [TestInitialize]
    public void Setup()
    {
        _root = new DirectoryInfo(TestPaths.TempRoot(nameof(WindowsSandboxAgentCompatibilityTests)));
        _root.Create();

        _directories = new TargetStateDirectoryProvider(_root.FullName);
        _stateStore = new TargetStateStore(_directories);
        _cli = new CompatibilitySandboxCli();

        _hostBinary = new FileInfo(Path.Join(_root.FullName, GuestAgentInstaller.BinaryName));
        File.WriteAllText(_hostBinary.FullName, "host-agent");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A loopback connection can briefly keep a test file open while the agent drains.
        }
    }

    [TestMethod]
    public async Task WarmReconnect_SamePersistedHash_ReusesTheAgentAcrossHostProcesses()
    {
        await using var agent = new LocalAgent();
        _cli.LaunchAgentHandler = (_, _, cancellationToken) =>
            agent.StartFromBootstrapAsync(TargetRoot, CurrentEpoch, cancellationToken);

        var first = await CreateBackend(agent.ReservePort).EnsureConnectedAsync(
            new EnsureTargetOptions(true),
            TestContext.CancellationToken);
        await first.Transport.DisposeAsync();

        // A new store and backend model a new CLI process: only the committed state and bootstrap
        // material connect the second process to the already-running agent.
        var persisted = new TargetStateStore(_directories).Read(Target);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(CurrentEpoch.Value, persisted.BootstrappedEpoch);
        Assert.AreEqual(VersionHelper.GetVersionString(), persisted.AgentVersion);
        Assert.AreEqual(
            await GuestAgentIdentity.ComputeBinaryHashAsync(_hostBinary.FullName, TestContext.CancellationToken),
            persisted.AgentBinaryHash);

        await using var channel = IntoCommandChannel(
            await CreateBackend().EnsureConnectedAsync(
                new EnsureTargetOptions(true),
                TestContext.CancellationToken));

        Assert.AreEqual("x64", (await channel.GetCapabilitiesAsync(TestContext.CancellationToken)).Architecture);
        Assert.AreEqual(1, _cli.LaunchAgentCount, "The second host must reuse instead of relaunching.");
    }

    [TestMethod]
    public async Task WarmReconnect_DifferentHostBinaryWithActiveChannels_RefusesWithoutRepairing()
    {
        await using var agent = new LocalAgent();
        var material = agent.StartNew(
            Target,
            Epoch,
            new GuestAgentIdentity(
                VersionHelper.GetVersionString(),
                "guest-hash",
                "x64",
                GuestProtocol.MinimumVersion,
                GuestProtocol.CurrentVersion));
        PersistWarmState(material, VersionHelper.GetVersionString(), "guest-hash");

        await using var firstActiveChannel = await ConnectCommandChannelAsync(material);
        await using var secondActiveChannel = await ConnectCommandChannelAsync(material);
        await firstActiveChannel.GetCapabilitiesAsync(TestContext.CancellationToken);
        await secondActiveChannel.GetCapabilitiesAsync(TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            CreateBackend().EnsureConnectedAsync(
                new EnsureTargetOptions(true),
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
        StringAssert.Contains(failure.Error.UserAction!, "Close Windows Sandbox");
        Assert.AreEqual(0, _cli.LaunchAgentCount, "A live mismatched agent must not be replaced.");

        // A different host arriving must not disrupt the channels the current agent is already serving.
        Assert.AreEqual(
            "x64",
            (await firstActiveChannel.GetCapabilitiesAsync(TestContext.CancellationToken)).Architecture);
        Assert.AreEqual(
            "x64",
            (await secondActiveChannel.GetCapabilitiesAsync(TestContext.CancellationToken)).Architecture);
    }

    [TestMethod]
    public async Task WarmReconnect_NewerCompatibleGuest_IsReusedWithoutDowngrade()
    {
        await using var agent = new LocalAgent();
        var material = agent.StartNew(
            Target,
            Epoch,
            new GuestAgentIdentity(
                "999.0.0",
                "newer-guest-hash",
                "x64",
                GuestProtocol.MinimumVersion,
                GuestProtocol.CurrentVersion));
        PersistWarmState(material, "999.0.0", "newer-guest-hash");

        await using var channel = IntoCommandChannel(
            await CreateBackend().EnsureConnectedAsync(
                new EnsureTargetOptions(true),
                TestContext.CancellationToken));

        Assert.AreEqual("x64", (await channel.GetCapabilitiesAsync(TestContext.CancellationToken)).Architecture);
        Assert.AreEqual(0, _cli.LaunchAgentCount, "A newer compatible guest must never be downgraded.");
    }

    [TestMethod]
    public async Task WarmReconnect_NewerUnavailableGuest_DoesNotDowngradeDuringRepair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var material = GuestBootstrapMaterial.Create(Target, Epoch, port);
        PersistWarmState(material, "999.0.0", "newer-guest-hash");

        var dropping = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
            client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            listener.Stop();
        }, TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            CreateBackend().EnsureConnectedAsync(
                new EnsureTargetOptions(true),
                TestContext.CancellationToken));

        await dropping;

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
        Assert.IsNull(failure.Error.NextCommand);
        StringAssert.Contains(failure.Error.UserAction!, "newer winapp release");
        Assert.AreEqual(0, _cli.LaunchAgentCount, "Repair must not replace a newer guest with this host.");
    }

    [TestMethod]
    public async Task WarmReconnect_NewerGuestThatDropsAHandshake_IsReportedAsBusy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var material = GuestBootstrapMaterial.Create(
            Target,
            Epoch,
            ((IPEndPoint)listener.LocalEndpoint).Port);
        PersistWarmState(material, "999.0.0", "newer-guest-hash");

        var dropsThenProvesLiveness = Task.Run(async () =>
        {
            using (var dropped = await listener.AcceptTcpClientAsync(TestContext.CancellationToken))
            {
                dropped.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            }

            // The backend asks whether the agent is still listening to distinguish a channel-cap
            // refusal from an agent that died during the handshake.
            using var livenessProbe = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
        }, TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            CreateBackend().EnsureConnectedAsync(
                new EnsureTargetOptions(true),
                TestContext.CancellationToken));

        await dropsThenProvesLiveness;

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentBusy, failure.Error.Code);
        Assert.AreEqual(0, _cli.LaunchAgentCount);
    }

    [TestMethod]
    public async Task WarmReconnect_MissingPersistedIdentity_RefusesTheLiveAgent()
    {
        await using var agent = new LocalAgent();
        var material = agent.StartNew(
            Target,
            Epoch,
            new GuestAgentIdentity(
                VersionHelper.GetVersionString(),
                "unknown-guest-hash",
                "x64",
                GuestProtocol.MinimumVersion,
                GuestProtocol.CurrentVersion));
        PersistWarmState(material, agentVersion: null, agentHash: null);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            CreateBackend().EnsureConnectedAsync(
                new EnsureTargetOptions(true),
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
        Assert.AreEqual("unknown", failure.Error.Context!["guestVersion"]);
        Assert.AreEqual(0, _cli.LaunchAgentCount, "An old state record must not silently reuse or replace a live agent.");
    }

    [TestMethod]
    public async Task WarmReconnect_StoppedMismatchedAgent_RebootstrapsWithinTheSameEpoch()
    {
        var unusedPort = FindUnusedLoopbackPort();
        var material = GuestBootstrapMaterial.Create(Target, Epoch, unusedPort);
        PersistWarmState(material, VersionHelper.GetVersionString(), "stopped-guest-hash");

        await using var agent = new LocalAgent();
        _cli.LaunchAgentHandler = (_, _, cancellationToken) =>
            agent.StartFromBootstrapAsync(TargetRoot, CurrentEpoch, cancellationToken);

        var connection = await CreateBackend(agent.ReservePort).EnsureConnectedAsync(
            new EnsureTargetOptions(true),
            TestContext.CancellationToken);
        await connection.Transport.DisposeAsync();

        var state = _stateStore.Read(Target);
        Assert.IsNotNull(state);
        Assert.AreEqual(Epoch.Value, connection.Epoch.Value);
        Assert.AreEqual(Epoch.Value, state.BootstrappedEpoch);
        Assert.AreEqual(
            await GuestAgentIdentity.ComputeBinaryHashAsync(_hostBinary.FullName, TestContext.CancellationToken),
            state.AgentBinaryHash);
        Assert.AreEqual(1, _cli.LaunchAgentCount, "A stopped agent is safe to repair in the current epoch.");
    }

    private WindowsSandboxBackend CreateBackend(Func<int>? agentPortProvider = null)
    {
        var backend = new WindowsSandboxBackend(
            _cli,
            new WindowsSandboxLifecycle(_cli, _stateStore),
            _directories,
            new StaticBinaryProvider(_hostBinary),
            new NoOpWindowController(),
            setup: null,
            _stateStore,
            NullTargetProgress.Instance);

        if (agentPortProvider is not null)
        {
            backend.AgentPortProvider = agentPortProvider;
        }

        return backend;
    }

    private void PersistWarmState(
        GuestBootstrapMaterial material,
        string? agentVersion,
        string? agentHash)
    {
        _cli.SetRunning(InstanceId);
        _stateStore.Commit(
            Target,
            new TargetState
            {
                SchemaVersion = TargetStateStore.CurrentSchemaVersion,
                Revision = 0,
                TargetKind = Target.Kind,
                TargetId = Target.Id,
                InstanceId = InstanceId,
                BootNonce = BootNonce,
                BootstrappedEpoch = Epoch.Value,
                AgentVersion = agentVersion,
                AgentBinaryHash = agentHash,
                GuestAddress = Loopback,
            },
            expectedRevision: 0);

        var bootstrap = Path.Join(TargetRoot, "bootstrap-" + WindowsSandboxBackend.EpochToken(Epoch));
        Directory.CreateDirectory(bootstrap);
        File.WriteAllText(Path.Join(bootstrap, GuestBootstrapMaterial.FileName), material.ToJson());
    }

    private static GuestCommandChannel IntoCommandChannel(TargetConnection connection)
    {
        var channel = new GuestCommandChannel(connection.Transport, connection.Epoch);
        channel.Start();
        return channel;
    }

    private static async Task<GuestCommandChannel> ConnectCommandChannelAsync(GuestBootstrapMaterial material)
    {
        var transport = await GuestTcpTransport.ConnectAsync(Loopback, material, CancellationToken.None);
        var channel = new GuestCommandChannel(transport, new ExecutionTargetEpoch(material.TargetEpoch));
        channel.Start();
        return channel;
    }

    private static int FindUnusedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class LocalAgent : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown = new();
        private readonly FakeGuestProcessHostFactory _processes = new();

        private TcpListener? _listener;
        private TcpListener? _reservedListener;
        private Task? _serving;

        public int ReservePort()
        {
            if (_reservedListener is not null)
            {
                throw new InvalidOperationException("A test-agent port is already reserved.");
            }

            _reservedListener = new TcpListener(IPAddress.Loopback, 0);
            _reservedListener.Start();
            return ((IPEndPoint)_reservedListener.LocalEndpoint).Port;
        }

        public GuestBootstrapMaterial StartNew(
            ExecutionTargetRef target,
            ExecutionTargetEpoch epoch,
            GuestAgentIdentity identity)
        {
            var (listener, port) = GuestTcpTransport.Listen(0, IPAddress.Loopback);
            var material = GuestBootstrapMaterial.Create(target, epoch, port);
            Start(material, epoch, identity, listener, listenerAlreadyStarted: true);
            return material;
        }

        public async Task StartFromBootstrapAsync(
            string targetRoot,
            ExecutionTargetEpoch epoch,
            CancellationToken cancellationToken)
        {
            var token = WindowsSandboxBackend.EpochToken(epoch);
            var bootstrap = Path.Join(targetRoot, "bootstrap-" + token);
            var material = GuestBootstrapMaterial.TryParse(
                await File.ReadAllTextAsync(
                    Path.Join(bootstrap, GuestBootstrapMaterial.FileName),
                    cancellationToken)) ?? throw new InvalidOperationException("The test agent could not read bootstrap material.");
            var identity = new GuestAgentIdentity(
                VersionHelper.GetVersionString(),
                await GuestAgentIdentity
                    .ComputeBinaryHashAsync(
                        Path.Join(bootstrap, GuestAgentInstaller.BinaryName),
                        cancellationToken)
                    .ConfigureAwait(false),
                "x64",
                GuestProtocol.MinimumVersion,
                GuestProtocol.CurrentVersion);

            var listenerAlreadyStarted = _reservedListener is not null;
            var listener = _reservedListener ?? new TcpListener(IPAddress.Loopback, material.Port);
            _reservedListener = null;

            if (listenerAlreadyStarted &&
                ((IPEndPoint)listener.LocalEndpoint).Port != material.Port)
            {
                listener.Stop();
                throw new InvalidOperationException("The backend did not use the test agent's reserved port.");
            }

            Start(material, epoch, identity, listener, listenerAlreadyStarted);

            var result = Path.Join(targetRoot, "bootstrap-result-" + token);
            Directory.CreateDirectory(result);
            await File.WriteAllTextAsync(
                Path.Join(result, WindowsSandboxBackend.HeartbeatFileName),
                GuestAgentHeartbeat.Create(
                    identity,
                    GuestReadinessFailure.None,
                    epoch,
                    material.Port,
                    DateTimeOffset.UtcNow).ToJson(),
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener?.Stop();
            _listener?.Dispose();
            _reservedListener?.Stop();
            _reservedListener?.Dispose();

            if (_serving is not null)
            {
                await _serving.ConfigureAwait(false);
            }

            _shutdown.Dispose();
        }

        private void Start(
            GuestBootstrapMaterial material,
            ExecutionTargetEpoch epoch,
            GuestAgentIdentity identity,
            TcpListener listener,
            bool listenerAlreadyStarted)
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException("The test agent is already running.");
            }

            _listener = listener;
            if (!listenerAlreadyStarted)
            {
                _listener.Start();
            }
            var acceptor = new GuestConnectionAcceptor(
                new GuestTcpConnectionSource(_listener, material),
                (transport, refusal) => new GuestCommandServer(
                    transport,
                    epoch,
                    _processes,
                    new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                    identity)
                {
                    AdmissionRefusal = refusal,
                });
            _serving = acceptor.RunAsync(_shutdown.Token);
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

    private sealed class CompatibilitySandboxCli : IWindowsSandboxCli
    {
        private readonly List<string> _running = [];

        public bool IsAvailable => true;

        public int LaunchAgentCount { get; private set; }

        public Func<string, string, CancellationToken, Task>? LaunchAgentHandler { get; set; }

        public void SetRunning(params string[] instanceIds)
        {
            _running.Clear();
            _running.AddRange(instanceIds);
        }

        public void UseExecutable(string executablePath)
        {
        }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. _running]);

        public Task<string> StartAsync(string instanceId, string? configuration, CancellationToken cancellationToken)
        {
            _running.Add(instanceId);
            return Task.FromResult(instanceId);
        }

        public Task StopAsync(string id, CancellationToken cancellationToken)
        {
            _running.Remove(id);
            return Task.CompletedTask;
        }

        public Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_running.Contains(id, StringComparer.OrdinalIgnoreCase));

        public Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Loopback);

        public Task<GuestSessionAvailability> ProbeInteractiveSessionAsync(
            string id,
            CancellationToken cancellationToken) =>
            Task.FromResult(GuestSessionAvailability.NoLoginSession);

        public Task ShareFolderAsync(
            string id,
            string hostPath,
            string sandboxPath,
            bool allowWrite,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ConnectAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> ExecuteAsync(
            string id,
            string command,
            string? workingDirectory,
            bool asSystem,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task LaunchAgentAsync(string id, string command, CancellationToken cancellationToken)
        {
            LaunchAgentCount++;
            return LaunchAgentHandler?.Invoke(id, command, cancellationToken) ?? Task.CompletedTask;
        }
    }
}

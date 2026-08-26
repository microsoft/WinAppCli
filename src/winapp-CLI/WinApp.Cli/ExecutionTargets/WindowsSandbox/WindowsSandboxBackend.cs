// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// The Windows Sandbox execution-target backend (spec §"Target backend").
/// </summary>
/// <remarks>
/// Everything Windows Sandbox specific lives here and in <see cref="WindowsSandboxCli"/>: instance
/// lifecycle, the read-only bootstrap share, guest IP discovery, and the interactive client. Above
/// this boundary orchestration sees only an <see cref="IGuestTransport"/> and an epoch, which is
/// what lets a future Hyper-V or remote backend reuse deployment, runtime, UI, and artifact handling
/// unchanged.
/// <para>
/// <c>wsb exec</c> is used for exactly one thing: launching the agent. It takes the command as a
/// single string and relays only an exit code, so it can neither carry argument boundaries nor
/// report what went wrong. The agent therefore writes its own diagnostics into the writable
/// bootstrap-result folder, and a failure to start surfaces those rather than a bare transport
/// error.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxBackend(
    IWindowsSandboxCli cli,
    WindowsSandboxLifecycle lifecycle,
    ITargetStateDirectoryProvider directoryProvider,
    IHostWinappBinaryProvider hostBinaryProvider,
    IWindowsSandboxWindowController windowController,
    ITargetStateStore? stateStore = null,
    ITargetProgress? progress = null) : IExecutionTargetBackend
{
    private readonly ITargetProgress _progress = progress ?? NullTargetProgress.Instance;

    /// <summary>Guest path the read-only bootstrap folder is mapped to.</summary>
    internal const string GuestBootstrapPath = @"C:\WinAppBootstrap";

    /// <summary>Guest path the writable bootstrap-result folder is mapped to.</summary>
    internal const string GuestResultPath = @"C:\WinAppBootstrapResult";

    /// <summary>Host folder name for the read-only bootstrap share.</summary>
    private const string BootstrapFolder = "bootstrap";

    /// <summary>Host folder name for the writable bootstrap-result share.</summary>
    private const string ResultFolder = "bootstrap-result";

    /// <summary>Native image encoder shipped beside the AOT CLI.</summary>
    private const string SkiaCompanionName = "libSkiaSharp.dll";

    /// <summary>How long the host waits for the agent to publish a readiness heartbeat.</summary>
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Delay while the connected client is still establishing the interactive login.</summary>
    internal static readonly TimeSpan AgentLaunchRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long to spend deciding whether an agent that closed a handshake is still listening.
    /// </summary>
    /// <remarks>
    /// Short on purpose. It only runs on a failure path, and it answers a question a live local
    /// agent settles in milliseconds; waiting longer would just delay the repair a dead one needs.
    /// </remarks>
    internal static readonly TimeSpan AgentLivenessProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Most files the untrusted result folder may contain before it is refused.</summary>
    internal const int MaxResultFiles = 16;

    /// <summary>Largest total size the untrusted result folder may reach before it is refused.</summary>
    internal const long MaxResultBytes = 1024 * 1024;

    /// <summary>
    /// Lowest port the guest agent is asked to listen on.
    /// </summary>
    /// <remarks>
    /// The IANA dynamic/private range. The host picks the port rather than letting the agent bind
    /// port 0, because the inbound firewall rule has to exist <em>before</em> the listener starts —
    /// see <see cref="AllowGuestAgentConnectionAsync"/> — and a rule cannot name a port nobody has
    /// chosen yet.
    /// </remarks>
    internal const int MinAgentPort = 49152;

    /// <summary>Highest port the guest agent is asked to listen on.</summary>
    internal const int MaxAgentPort = 65535;

    private string? _instanceId;
    private string? _guestAddress;
    private GuestBootstrapMaterial? _activeMaterial;

    /// <summary>Picks a listening port for the guest agent from the dynamic range.</summary>
    /// <remarks>
    /// Random rather than fixed so two unrelated guests do not collide on a well-known number, and
    /// so a stale rule from a previous boot does not silently authorise a new agent. A collision
    /// inside the guest is handled by retrying the bootstrap with a fresh port rather than by
    /// falling back to port 0, which would reintroduce the ordering problem.
    /// </remarks>
    internal static int NextAgentPort() =>
        RandomNumberGenerator.GetInt32(MinAgentPort, MaxAgentPort + 1);

    /// <inheritdoc/>
    public ExecutionTargetRef Target => ExecutionTargetRef.WindowsSandboxDefault;

    /// <inheritdoc/>
    public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(TargetSupportResult.Unsupported(new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.Unsupported,
                Message = "Windows Sandbox execution requires Windows.",
                UserAction = "Run this command on a Windows 11 machine with Windows Sandbox installed.",
            }));
        }

        // Probed before the application is built, so a missing prerequisite fails in seconds rather
        // than after a long build. There is never a silent fallback to local execution.
        if (!cli.IsAvailable)
        {
            return Task.FromResult(TargetSupportResult.Unsupported(new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.Unsupported,
                Message = "The Windows Sandbox command line (wsb.exe) was not found.",
                UserAction =
                    "Install the Windows Sandbox optional feature on Windows 11 24H2 or newer, then retry.",
                NextCommand = new ExecutionTargetNextCommand
                {
                    Command = "Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM",

                    // Enabling a Windows feature needs elevation and a reboot, so it is the user's
                    // decision, never something winapp performs.
                    Advisory = true,
                },
                Example = "winapp run . --sandbox",
            }));
        }

        return Task.FromResult(TargetSupportResult.Supported);
    }

    /// <inheritdoc/>
    public async Task<TargetConnection> EnsureConnectedAsync(
        EnsureTargetOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lease = await lifecycle.EnsureInstanceAsync(cancellationToken).ConfigureAwait(false);
        _instanceId = lease.InstanceId;

        // Reconnecting to an agent that is already serving is the whole point of a persistent
        // Sandbox: it costs one TCP connect instead of a client reconnect, an agent relaunch, and a
        // runtime re-verification. It is attempted before anything else touches the instance.
        if (lease.Reused &&
            await TryReconnectAsync(lease, cancellationToken).ConfigureAwait(false) is { } reused)
        {
            _progress.Report("Reusing the running Windows Sandbox agent...");
            return reused;
        }

        _progress.Report(lease.Reused
            ? "Repairing the Windows Sandbox agent..."
            : "Starting Windows Sandbox...");

        var bootstrap = PrepareBootstrapDirectories(lease.Epoch);
        var agentHash = await StageBootstrapBinaryAsync(bootstrap.HostBootstrap, cancellationToken)
            .ConfigureAwait(false);

        // The port is chosen here, by the host, and written into the material the agent reads. That
        // ordering is what removes the Windows Security consent dialog: the inbound rule below can
        // only be created for a port that is already known, and Windows prompts the moment a
        // listener starts without a matching rule.
        var agentPort = NextAgentPort();
        var material = GuestBootstrapMaterial.Create(Target, lease.Epoch, agentPort);
        await File.WriteAllTextAsync(
            Path.Join(bootstrap.HostBootstrap, GuestBootstrapMaterial.FileName),
            material.ToJson(),
            cancellationToken).ConfigureAwait(false);

        // Read-only: the guest must not be able to rewrite the connection material and redirect its
        // own agent. The result folder is the only writable path, and it is bounded and treated as
        // untrusted input.
        await cli.ShareFolderAsync(
            lease.InstanceId, bootstrap.HostBootstrap, GuestBootstrapPath, allowWrite: false, cancellationToken)
            .ConfigureAwait(false);

        await cli.ShareFolderAsync(
            lease.InstanceId, bootstrap.HostResult, GuestResultPath, allowWrite: true, cancellationToken)
            .ConfigureAwait(false);

        // Real input and Windows Graphics Capture need a connected client. Connecting is also what
        // establishes the interactive user session the agent must run in, so it happens before the
        // agent is launched rather than after.
        //
        // Only ever on a bootstrap. Calling this against an instance whose client is already up
        // tears that client's session down and shows the user "the connection was lost, reconnect?",
        // which is why the reuse path above must be tried first.
        if (options.RequireInteractiveDesktop || !lease.Reused)
        {
            _progress.Report("Connecting the Windows Sandbox window...");
            var windowSnapshot = windowController.Capture();
            await cli.ConnectAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);
            await windowController
                .PlaceConnectedClientAsync(windowSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }

        // Once per generation, before the first application is ever deployed. Registering a loose
        // layout needs Developer Mode, and it is a machine-wide setting a guest process running as
        // the interactive user cannot set — so it is done here, as one fixed privileged operation
        // with a constant command, rather than by exposing SYSTEM execution.
        if (!lease.Reused)
        {
            await EnableGuestDevelopmentModeAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);
        }

        // Before the agent starts listening, not after. This is the fix for the firewall consent
        // prompt: the rule has to be in place when the socket opens, or Windows asks the user.
        await AllowGuestAgentConnectionAsync(
            lease.InstanceId,
            agentPort,
            cancellationToken).ConfigureAwait(false);

        _progress.Report("Preparing the Windows Sandbox agent...");

        var heartbeat = await LaunchReadyAgentAsync(
            lease.InstanceId,
            bootstrap.HostResult,
            lease.Epoch,
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(heartbeat.BinaryHash, agentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "The Windows Sandbox agent is not the winapp binary this host staged.",
                userAction: "Close Windows Sandbox, then retry so winapp starts a fresh guest agent.",
                context: new Dictionary<string, string>
                {
                    ["expectedHash"] = agentHash,
                    ["actualHash"] = heartbeat.BinaryHash,
                });
        }

        if (heartbeat.Port != agentPort)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "The Windows Sandbox agent is listening on a port the host did not authorise.",
                userAction: "Close Windows Sandbox, then retry.",
                context: new Dictionary<string, string>
                {
                    ["expectedPort"] = agentPort.ToString(CultureInfo.InvariantCulture),
                    ["actualPort"] = heartbeat.Port.ToString(CultureInfo.InvariantCulture),
                });
        }

        _guestAddress = await cli.GetIpAddressAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);

        _progress.Report("Connecting to the Windows Sandbox agent...");

        var transport = await GuestTcpTransport.ConnectAsync(
            _guestAddress,
            material,
            cancellationToken).ConfigureAwait(false);
        _activeMaterial = material;

        RememberConnection(lease, _guestAddress);

        // The result folder has served its purpose and is guest-writable, so it does not survive the
        // handshake it was created for.
        TryClearResultFolder(bootstrap.HostResult);

        return new TargetConnection(lease.Epoch, transport, lease.Reused);
    }

    /// <summary>
    /// Reconnects to an agent that is already serving this epoch, or returns null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every CLI invocation constructs a fresh backend, so in-process fields alone can never make
    /// the second command reuse the first one's agent — the material has to come off disk. It is
    /// read back from the same read-only bootstrap file the agent itself was given, so host and
    /// guest cannot disagree about the key, and the epoch inside it is checked against the lease so
    /// material from a previous boot is never used against a new one.
    /// </para>
    /// <para>
    /// Returning null means "reconnect was not possible", never "the Sandbox is unusable": the
    /// caller falls through to a full bootstrap, which repairs the agent without recreating the
    /// instance. A failure here must not be reported as an error, because a stopped agent is an
    /// ordinary state after the guest reboots or the agent is killed.
    /// </para>
    /// </remarks>
    private async Task<TargetConnection?> TryReconnectAsync(
        SandboxInstanceLease lease,
        CancellationToken cancellationToken)
    {
        var material = _activeMaterial ?? TryReadPersistedMaterial();

        // Port 0 is what an older build wrote before the host assigned ports, and it is also what
        // "bind anywhere" means — never a port anything is listening on. Treated as no material at
        // all so a stale file makes this repair rather than attempt a meaningless connect.
        if (material is null ||
            !string.Equals(material.TargetEpoch, lease.Epoch.Value, StringComparison.Ordinal) ||
            material.Port is <= 0 or > IPEndPoint.MaxPort)
        {
            return null;
        }

        var address = _guestAddress ?? TryReadPersistedAddress(lease);

        if (string.IsNullOrWhiteSpace(address))
        {
            try
            {
                address = await cli.GetIpAddressAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionTargetException)
            {
                return null;
            }
        }

        try
        {
            var transport = await GuestTcpTransport
                .ConnectAsync(address, material, cancellationToken)
                .ConfigureAwait(false);

            _guestAddress = address;
            _activeMaterial = material;
            RememberConnection(lease, address);

            return new TargetConnection(lease.Epoch, transport, Reused: true);
        }
        catch (ExecutionTargetException ex)
        {
            _activeMaterial = null;

            // A peer that accepted the connection and then closed it was alive a moment ago, which
            // an agent at its channel ceiling is and a dead agent is not. The two are identical on
            // the wire, so they are told apart by asking whether anything is still listening rather
            // than by guessing. Repairing a live agent would stage and relaunch one underneath the
            // channels the running agent is still serving.
            if (ex.Error.Context?.ContainsKey(GuestSecureChannel.ClosedDuringHandshakeKey) == true &&
                await GuestTcpTransport.IsListeningAsync(
                    address, material.Port, AgentLivenessProbeTimeout, cancellationToken).ConfigureAwait(false))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.AgentBusy,
                    "The Windows Sandbox agent is serving as many winapp commands as it allows.",
                    userAction: "Wait for one of the running commands to finish, then retry.",
                    innerException: ex);
            }

            // The Sandbox is alive but its agent is not answering. Repair only that layer; the
            // unchanged epoch keeps deployment and runtime state valid across the repair.
            return null;
        }
    }

    /// <summary>Reads the connection material this target last bootstrapped with.</summary>
    private GuestBootstrapMaterial? TryReadPersistedMaterial()
    {
        try
        {
            var path = Path.Join(
                directoryProvider.GetTargetRoot(Target, create: false).FullName,
                BootstrapFolder,
                GuestBootstrapMaterial.FileName);

            return File.Exists(path) ? GuestBootstrapMaterial.TryParse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ExecutionTargetException)
        {
            return null;
        }
    }

    /// <summary>The guest address recorded alongside this instance, when it is still the same one.</summary>
    private string? TryReadPersistedAddress(SandboxInstanceLease lease)
    {
        var state = stateStore?.Read(Target);

        return string.Equals(state?.InstanceId, lease.InstanceId, StringComparison.OrdinalIgnoreCase)
            ? state?.GuestAddress
            : null;
    }

    /// <summary>
    /// Records the guest address so the next process can reconnect without re-querying it.
    /// </summary>
    /// <remarks>
    /// Best effort and never fatal. The address is a cache, not a source of truth — losing it costs
    /// one <c>wsb</c> call on the next command, while failing the command over it would trade a
    /// working Sandbox for an error.
    /// </remarks>
    private void RememberConnection(SandboxInstanceLease lease, string address)
    {
        if (stateStore is null || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        try
        {
            var state = stateStore.Read(Target);

            if (state is null ||
                !string.Equals(state.InstanceId, lease.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state.GuestAddress, address, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            stateStore.Commit(Target, state with { GuestAddress = address }, state.Revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExecutionTargetException)
        {
            // A stale or contended state file costs a re-query next time, nothing more.
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> DescribeForDiagnostics()
    {
        var description = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_instanceId is { } id)
        {
            description["sandboxId"] = id;
        }

        if (_guestAddress is { } address)
        {
            description["guestAddress"] = address;
        }

        return description;
    }

    /// <summary>Creates this generation's bootstrap folders without replacing mapped roots.</summary>
    private (string HostBootstrap, string HostResult) PrepareBootstrapDirectories(ExecutionTargetEpoch epoch)
    {
        var root = directoryProvider.GetTargetRoot(Target, create: true).FullName;
        var bootstrap = TargetPathSafety.CombineInsideRoot(root, BootstrapFolder);
        var result = TargetPathSafety.CombineInsideRoot(root, ResultFolder);

        // WSB holds mapped folder roots open for the lifetime of the Sandbox. Keep those exact roots
        // and replace their bounded contents instead; deleting a mapped root makes warm reuse fail
        // with ERROR_SHARING_VIOLATION before it can reconnect to the live agent.
        Directory.CreateDirectory(bootstrap);
        Directory.CreateDirectory(result);
        ClearDirectoryContents(result);

        _ = epoch;
        return (bootstrap, result);
    }

    private static void ClearDirectoryContents(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(path))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.GetDirectories(path))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Publishes the host binary into the read-only bootstrap share and returns its hash.</summary>
    /// <remarks>
    /// Staging is skipped entirely when the bytes already match, which is the ordinary case: the
    /// same build reconnecting or repairing does not rewrite the share at all. Replacing the file is
    /// therefore only attempted when the host binary genuinely changed — and that is exactly when
    /// the running agent may still hold the old one open.
    /// </remarks>
    private async Task<string> StageBootstrapBinaryAsync(
        string bootstrapDirectory,
        CancellationToken cancellationToken)
    {
        var source = hostBinaryProvider.GetBinary();

        string expected;
        try
        {
            expected = await StageBootstrapFileAsync(
                source,
                TargetPathSafety.CombineInsideRoot(bootstrapDirectory, GuestAgentInstaller.BinaryName),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // The agent already serving this Sandbox is running from this exact file, so a newer
            // winapp cannot replace it in place. Reported as the actionable thing it is: the raw
            // exception surfaces as "IO_SharingViolation_NoFileName", which tells the user nothing
            // and looks like a winapp defect rather than a running-agent conflict.
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "A different version of winapp is already running the Windows Sandbox agent, " +
                "so this one could not replace it.",
                userAction: "Close Windows Sandbox, then run the command again to start a fresh agent.",
                nextCommand: new ExecutionTargetNextCommand
                {
                    Command = "wsb stop",

                    // Stopping discards whatever is running in the guest, so it stays the user's call.
                    Advisory = true,
                },
                innerException: ex);
        }

        var companion = new FileInfo(Path.Join(source.DirectoryName, SkiaCompanionName));
        if (companion.Exists)
        {
            try
            {
                await StageBootstrapFileAsync(
                    companion,
                    TargetPathSafety.CombineInsideRoot(bootstrapDirectory, SkiaCompanionName),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The companion is only needed for image encoding, and a locked one is byte-identical
                // to what a running agent already loaded. Failing the whole command over it would
                // turn a working Sandbox into an error.
            }
        }

        return expected;
    }

    /// <summary>Atomically stages and verifies one trusted host runtime file.</summary>
    private static async Task<string> StageBootstrapFileAsync(
        FileInfo source,
        string destination,
        CancellationToken cancellationToken)
    {
        var staged = $"{destination}.{Guid.NewGuid():N}.tmp";

        try
        {
            var expected = await GuestAgentIdentity
                .ComputeBinaryHashAsync(source.FullName, cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(destination))
            {
                var existing = await GuestAgentIdentity
                    .ComputeBinaryHashAsync(destination, cancellationToken)
                    .ConfigureAwait(false);

                if (string.Equals(expected, existing, StringComparison.OrdinalIgnoreCase))
                {
                    return expected;
                }
            }

            await using (var input = new FileStream(
                source.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 128 * 1024,
                useAsync: true))
            await using (var output = new FileStream(
                staged,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var actual = await GuestAgentIdentity
                .ComputeBinaryHashAsync(staged, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.AgentUpgradeFailed,
                    $"The Windows Sandbox runtime file '{source.Name}' changed while it was staged.",
                    userAction: "Retry the command. If it keeps failing, reinstall winapp.",
                    context: new Dictionary<string, string>
                    {
                        ["expectedHash"] = expected,
                        ["actualHash"] = actual,
                    });
            }

            File.Move(staged, destination, overwrite: true);
            return expected;
        }
        catch
        {
            AtomicFile.DiscardStaged(staged);
            throw;
        }
    }

    /// <summary>
    /// Enables loose-package registration in a fresh guest (spec §"Framework support").
    /// </summary>
    /// <remarks>
    /// A fixed executable with a fixed argument string, exactly as the specification requires of the
    /// privileged setup winapp needs: it is not a general SYSTEM escape hatch and nothing about it
    /// is caller-influenced.
    /// <para>
    /// A failure is reported as a warning rather than failing the connection. Copying files and
    /// running commands in the guest do not need this, and refusing those because a registration
    /// prerequisite could not be set would turn one broken capability into all of them. A packaged
    /// run then fails with guest winapp's own Developer Mode message, which names the real problem.
    /// </para>
    /// </remarks>
    private async Task EnableGuestDevelopmentModeAsync(string instanceId, CancellationToken cancellationToken)
    {
        const string Command =
            @"reg.exe add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"" " +
            @"/f /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1";

        try
        {
            var exitCode = await cli.ExecuteAsync(
                instanceId,
                Command,
                workingDirectory: null,

                // The value lives under HKLM, which the interactive guest user cannot write
                // unelevated. This is the one operation that needs it.
                asSystem: true,
                cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Could not enable Developer Mode in Windows Sandbox (exit code {0}); packaged runs may fail to register.",
                    exitCode);
            }
        }

        catch (ExecutionTargetException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not enable Developer Mode in Windows Sandbox: {0}", ex.Message);
        }
    }

    /// <summary>Allows inbound TCP only to the staged guest agent on its pre-assigned port.</summary>
    /// <remarks>
    /// <para>
    /// Windows Sandbox blocks the host-to-guest connection by default. This is the second and only
    /// other privileged bootstrap operation: the executable, direction, protocol, action, and
    /// profile are constants; the sole dynamic value is the integer port the host chose.
    /// Authentication and encryption still gate every request after the socket opens.
    /// </para>
    /// <para>
    /// <b>This must run before the agent starts listening.</b> Windows raises the "Windows Firewall
    /// has blocked some features of this app" consent dialog at the moment a program binds a
    /// listening socket with no matching rule — so a rule created afterwards, however correct,
    /// arrives too late to prevent the prompt and leaves the user staring at a dialog inside the
    /// Sandbox window. That ordering is why the port is chosen by the host rather than by binding
    /// port 0 and reading back what the agent got.
    /// </para>
    /// <para>
    /// The rule is scoped to the agent program <em>and</em> the single port it was told to use, so
    /// nothing else in the guest gains inbound reachability. Stale allow rules from earlier boots
    /// are removed first, so a port this guest is no longer using does not stay open.
    /// </para>
    /// </remarks>
    private async Task AllowGuestAgentConnectionAsync(
        string instanceId,
        int port,
        CancellationToken cancellationToken)
    {
        if (port is <= 0 or > IPEndPoint.MaxPort)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "The Windows Sandbox agent was assigned an invalid TCP port.",
                userAction: "Close Windows Sandbox, then retry.",
                context: new Dictionary<string, string>
                {
                    ["port"] = port.ToString(CultureInfo.InvariantCulture),
                });
        }

        var portText = port.ToString(CultureInfo.InvariantCulture);
        var agentPath = $@"{GuestBootstrapPath}\{GuestAgentInstaller.BinaryName}";
        var command =
            @"powershell.exe -NoProfile -NonInteractive -Command " +
            $@"""$agent='{agentPath}'; " +

            // Any existing rule for this program is removed first, block or allow: a block rule
            // would defeat the new allow, and a stale allow would leave a port open that this boot
            // is not using.
            @"$existing=Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { " +
            @"($_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue).Program -eq $agent }; " +
            @"foreach ($rule in $existing) { " +
            @"Remove-NetFirewallRule -Name $rule.Name -PolicyStore ActiveStore -ErrorAction SilentlyContinue; " +
            @"Remove-NetFirewallRule -Name $rule.Name -PolicyStore PersistentStore -ErrorAction SilentlyContinue }; " +
            $@"New-NetFirewallRule -DisplayName 'WinApp Sandbox Agent {portText}' " +
            $@"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {portText} -Program $agent " +
            @"-Profile Any -EdgeTraversalPolicy Allow -PolicyStore ActiveStore -ErrorAction Stop | Out-Null""";

        var exitCode = await cli.ExecuteAsync(
            instanceId,
            command,
            workingDirectory: null,
            asSystem: true,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "Windows Sandbox could not allow the authenticated guest-agent connection.",
                userAction: "Close Windows Sandbox, then retry.",
                context: new Dictionary<string, string>
                {
                    ["exitCode"] = exitCode.ToString(CultureInfo.InvariantCulture),
                    ["port"] = port.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>
    /// Starts the guest agent through <c>wsb exec</c>, the one fixed bootstrap operation.
    /// </summary>
    /// <remarks>
    /// The agent runs from the read-only share on first launch. It copies itself into guest-local
    /// storage before serving, so the host share is not held open for the life of the Sandbox.
    /// </remarks>
    private async Task LaunchAgentAsync(string instanceId, CancellationToken cancellationToken)
    {
        var command =
            $"\"{GuestBootstrapPath}\\{GuestAgentInstaller.BinaryName}\" {GuestAgentCommandNames.Verb} " +
            $"--bootstrap-dir \"{GuestBootstrapPath}\" --result-dir \"{GuestResultPath}\"";

        await cli.LaunchAgentAsync(instanceId, command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the agent once the connected client has made the login desktop input-ready.
    /// </summary>
    /// <remarks>
    /// The client process is necessarily launched without waiting for it to exit, and its window can
    /// appear before the guest input desktop is ready. An agent started in that interval reports
    /// <c>NoInputDesktop</c> and exits without accepting anything, so repeating that bootstrap is
    /// safe. No other failure is retried: once a command can be dispatched, its result is
    /// authoritative and repeating it could duplicate a side effect.
    /// </remarks>
    private async Task<GuestAgentHeartbeat> LaunchReadyAgentAsync(
        string instanceId,
        string resultDirectory,
        ExecutionTargetEpoch epoch,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + HeartbeatTimeout;

        while (true)
        {
            TryClearResultFolder(resultDirectory);

            try
            {
                await LaunchAgentAsync(instanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionTargetException ex) when (
                ex.Error.Code == ExecutionTargetErrorCodes.TransportFailed &&
                DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(AgentLaunchRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                return await WaitForHeartbeatAsync(resultDirectory, epoch, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ExecutionTargetException ex) when (
                ex.Error.Code == ExecutionTargetErrorCodes.NoInteractiveSession &&
                ex.Error.Context?.GetValueOrDefault("reason") == GuestReadinessFailure.NoInputDesktop.ToString() &&
                DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(AgentLaunchRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Waits for the agent's readiness heartbeat, reporting its own diagnostics when it refuses.
    /// </summary>
    /// <remarks>
    /// The agent publishes a heartbeat even when it is <em>not</em> ready, so a refusal to serve
    /// surfaces the exact reason — session 0, no input desktop — rather than as a timeout on
    /// silence. That is the whole reason the result folder exists: <c>wsb exec</c> returns only an
    /// exit code and can never carry it.
    /// </remarks>
    internal static async Task<GuestAgentHeartbeat> WaitForHeartbeatAsync(
        string resultDirectory,
        ExecutionTargetEpoch epoch,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + HeartbeatTimeout;
        var path = Path.Join(resultDirectory, HeartbeatFileName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureResultFolderWithinLimits(resultDirectory);

            if (TryReadHeartbeat(path) is { } heartbeat &&
                string.Equals(heartbeat.TargetEpoch, epoch.Value, StringComparison.Ordinal))
            {
                if (!heartbeat.Ready)
                {
                    throw ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.NoInteractiveSession,
                        "The Windows Sandbox agent started but is not able to serve commands.",
                        userAction: "Retry the command so winapp restarts the agent in the interactive session.",
                        context: new Dictionary<string, string>
                        {
                            ["reason"] = heartbeat.NotReadyReason ?? "unknown",
                        });
                }

                return heartbeat;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.StartFailed,
                    "The Windows Sandbox agent did not report ready in time.",
                    userAction: "Retry the command. If it keeps failing, close Windows Sandbox and try again.",
                    context: ReadStartupDiagnostics(resultDirectory));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>File the agent publishes its heartbeat to.</summary>
    internal const string HeartbeatFileName = "heartbeat.json";

    /// <summary>File the agent writes startup stdout and stderr to.</summary>
    internal const string StartupLogFileName = "startup.log";

    private static GuestAgentHeartbeat? TryReadHeartbeat(string path)
    {
        try
        {
            return File.Exists(path) ? GuestAgentHeartbeat.TryParse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            // The agent is mid-write. The next poll sees the complete file.
            return null;
        }
    }

    /// <summary>
    /// Refuses a result folder that has grown beyond its fixed limits.
    /// </summary>
    /// <remarks>
    /// This folder is the only guest-writable path the host reads, so it is treated as untrusted
    /// input. Bounding file count and total size stops a co-resident guest process from filling the
    /// host's disk through it, or from making the host read an unbounded amount while polling.
    /// </remarks>
    internal static void EnsureResultFolderWithinLimits(string resultDirectory)
    {
        if (!Directory.Exists(resultDirectory))
        {
            return;
        }

        var files = new DirectoryInfo(resultDirectory).GetFiles("*", SearchOption.AllDirectories);
        if (files.Length <= MaxResultFiles && files.Sum(f => f.Length) <= MaxResultBytes)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.StartFailed,
            "The Windows Sandbox startup folder exceeded its size limit and was not read.",
            userAction: "Close Windows Sandbox, then retry.",
            context: new Dictionary<string, string>
            {
                ["fileCount"] = files.Length.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Reads whatever the agent managed to say before it failed.</summary>
    private static Dictionary<string, string> ReadStartupDiagnostics(string resultDirectory)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal);
        var log = Path.Join(resultDirectory, StartupLogFileName);

        try
        {
            if (File.Exists(log))
            {
                var text = File.ReadAllText(log).Trim();

                // Bounded: this is untrusted guest output going into a host error envelope.
                context["guestOutput"] = text.Length > 2000 ? text[..2000] : text;
            }
        }
        catch (IOException)
        {
            // No diagnostics available; the timeout message still stands on its own.
        }

        return context;
    }

    private static void TryClearResultFolder(string resultDirectory)
    {
        try
        {
            ClearDirectoryContents(resultDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The guest still holds a staged diagnostic open. A stale report is epoch-checked before
            // use, so cleanup failure must not fail a connection that already succeeded.
            System.Diagnostics.Trace.TraceWarning(
                "Could not clear the Sandbox bootstrap result folder '{0}': {1}", resultDirectory, ex.Message);
        }
    }
}

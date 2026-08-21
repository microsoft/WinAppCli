// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net.Sockets;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

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
    ITargetStateDirectoryProvider directoryProvider) : IExecutionTargetBackend
{
    /// <summary>Guest path the read-only bootstrap folder is mapped to.</summary>
    internal const string GuestBootstrapPath = @"C:\WinAppBootstrap";

    /// <summary>Guest path the writable bootstrap-result folder is mapped to.</summary>
    internal const string GuestResultPath = @"C:\WinAppBootstrapResult";

    /// <summary>Host folder name for the read-only bootstrap share.</summary>
    private const string BootstrapFolder = "bootstrap";

    /// <summary>Host folder name for the writable bootstrap-result share.</summary>
    private const string ResultFolder = "bootstrap-result";

    /// <summary>How long the host waits for the agent to publish a readiness heartbeat.</summary>
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Most files the untrusted result folder may contain before it is refused.</summary>
    internal const int MaxResultFiles = 16;

    /// <summary>Largest total size the untrusted result folder may reach before it is refused.</summary>
    internal const long MaxResultBytes = 1024 * 1024;

    private string? _instanceId;
    private string? _guestAddress;

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

        var bootstrap = PrepareBootstrapDirectories(lease.Epoch);

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
        if (options.RequireInteractiveDesktop || !lease.Reused)
        {
            await cli.ConnectAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);
        }

        var material = GuestBootstrapMaterial.Create(Target, lease.Epoch, port: 0);
        await File.WriteAllTextAsync(
            Path.Join(bootstrap.HostBootstrap, GuestBootstrapMaterial.FileName),
            material.ToJson(),
            cancellationToken).ConfigureAwait(false);

        await LaunchAgentAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);

        var heartbeat = await WaitForHeartbeatAsync(bootstrap.HostResult, lease.Epoch, cancellationToken)
            .ConfigureAwait(false);

        _guestAddress = await cli.GetIpAddressAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);

        var transport = await GuestTcpTransport.ConnectAsync(
            _guestAddress,
            material with { Port = heartbeat.Port },
            cancellationToken).ConfigureAwait(false);

        // The result folder has served its purpose and is guest-writable, so it does not survive the
        // handshake it was created for.
        TryClearResultFolder(bootstrap.HostResult);

        return new TargetConnection(lease.Epoch, transport, lease.Reused);
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

    /// <summary>Creates this generation's bootstrap folders, discarding any previous contents.</summary>
    private (string HostBootstrap, string HostResult) PrepareBootstrapDirectories(ExecutionTargetEpoch epoch)
    {
        var root = directoryProvider.GetTargetRoot(Target, create: true).FullName;
        var bootstrap = TargetPathSafety.CombineInsideRoot(root, BootstrapFolder);
        var result = TargetPathSafety.CombineInsideRoot(root, ResultFolder);

        // A fresh start per generation: material from a previous boot authenticates nothing, and
        // leaving it around only invites a confusing failure later.
        RecreateDirectory(bootstrap);
        RecreateDirectory(result);

        _ = epoch;
        return (bootstrap, result);
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
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

        var exitCode = await cli.ExecuteAsync(
            instanceId,
            command,
            workingDirectory: null,
            asSystem: false,
            cancellationToken).ConfigureAwait(false);

        if (exitCode == 0)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.StartFailed,
            "The Windows Sandbox agent could not be started.",
            userAction: "Retry the command. If it keeps failing, close Windows Sandbox and try again.",
            context: new Dictionary<string, string>
            {
                ["exitCode"] = exitCode.ToString(CultureInfo.InvariantCulture),
            });
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
            if (Directory.Exists(resultDirectory))
            {
                Directory.Delete(resultDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The guest still holds it open. It is recreated from scratch next generation, so a
            // leftover is harmless and must not fail a connection that already succeeded.
            System.Diagnostics.Trace.TraceWarning(
                "Could not remove the Sandbox bootstrap result folder '{0}': {1}", resultDirectory, ex.Message);
        }
    }
}

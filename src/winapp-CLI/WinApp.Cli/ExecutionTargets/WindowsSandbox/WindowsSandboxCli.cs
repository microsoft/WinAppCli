// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// Invokes <c>wsb.exe</c> and parses its <c>--raw</c> JSON.
/// </summary>
/// <remarks>
/// Every argument is passed through <see cref="ProcessRunRequest"/>'s argument list rather than a
/// concatenated command line, so a value such as a path can never be smuggled in as an extra
/// option.
/// </remarks>
internal sealed class WindowsSandboxCli(IProcessRunner processRunner) : IWindowsSandboxCli
{
    /// <summary>Executable name searched for on PATH.</summary>
    internal const string ExecutableName = "wsb.exe";

    /// <summary>
    /// Fully qualified path to the trusted <c>wsb.exe</c>.
    /// </summary>
    /// <remarks>
    /// Launching by bare name would let <c>CreateProcess</c> search the application and current
    /// directories before PATH, so a <c>wsb.exe</c> dropped into a repository a developer happens to
    /// be sitting in would win. Resolving to an absolute path first and always executing that path
    /// removes the ambiguity.
    /// <para>
    /// Only a <em>successful</em> resolution is remembered. A failed one is retried on the next
    /// access, because prerequisite setup can make the alias appear during the very command that
    /// found it missing — which a <see cref="Lazy{T}"/> would have latched as unavailable for the
    /// rest of the process.
    /// </para>
    /// </remarks>
    private string? _executablePath;

    /// <summary>Starts the long-lived interactive client; seamed for argument-construction tests.</summary>
    internal Action<ProcessStartInfo> ConnectLauncher { get; set; } = LaunchConnectedClient;

    /// <inheritdoc/>
    public bool IsAvailable => Executable is not null;

    /// <inheritdoc/>
    public void UseExecutable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The Windows Sandbox executable must be an absolute path.",
                nameof(executablePath));
        }

        _executablePath = executablePath;
    }

    private string? Executable => _executablePath ??= WindowsSandboxHostProbe.ResolveTrustedAlias();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["list", "--raw"], cancellationToken).ConfigureAwait(false);
        var payload = Deserialize(result.StandardOutput, WindowsSandboxCliJsonContext.Default.WsbEnvironmentList);

        if (payload?.WindowsSandboxEnvironments is not { } environments)
        {
            return [];
        }

        return [.. environments.Select(e => e.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)];
    }

    /// <inheritdoc/>
    public async Task<string> StartAsync(
        string instanceId,
        string? configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        List<string> arguments = ["start", "--id", instanceId, "--raw"];
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            arguments.Add("--config");
            arguments.Add(configuration);
        }

        var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        var payload = Deserialize(result.StandardOutput, WindowsSandboxCliJsonContext.Default.WsbEnvironmentList);

        // Accept either shape: a bare root ID, or the same list wrapper `list` uses.
        var id = payload?.Id ?? payload?.WindowsSandboxEnvironments?.FirstOrDefault()?.Id;

        // A start that reports nothing has still, on this host, sometimes created the instance the
        // caller asked for. The caller assigned the ID, so it can reconcile that exact one rather
        // than guessing -- but it must be told the report was missing, not handed the ID back as if
        // wsb had confirmed it.
        if (string.IsNullOrWhiteSpace(id))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StartFailed,
                "Windows Sandbox started but did not report an instance ID.",
                userAction: "Retry the command. If it keeps failing, restart the host.",
                context: new Dictionary<string, string> { ["requestedId"] = instanceId });
        }

        return id;
    }

    /// <inheritdoc/>
    public Task StopAsync(string id, CancellationToken cancellationToken) =>
        RunAsync(["stop", "--id", id, "--raw"], cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await GetIpAddressAsync(id, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ExecutionTargetException)
        {
            // "Not resolvable yet" is an ordinary state for an instance that is still coming up or
            // already going away, so it is answered rather than reported.
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["ip", "--id", id, "--raw"], cancellationToken).ConfigureAwait(false);
        var payload = Deserialize(result.StandardOutput, WindowsSandboxCliJsonContext.Default.WsbNetworkList);

        var address = payload?.Networks?
            .Select(n => n.IpV4Address)
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

        if (string.IsNullOrWhiteSpace(address))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "Windows Sandbox did not report a guest IP address.",
                userAction: "Retry the command once the Sandbox has finished starting.",
                context: new Dictionary<string, string> { ["sandboxId"] = id });
        }

        return address;
    }

    /// <inheritdoc/>
    public Task ShareFolderAsync(
        string id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["share", "--id", id, "--host-path", hostPath, "--sandbox-path", sandboxPath, "--raw"];
        if (allowWrite)
        {
            arguments.Add("--allow-write");
        }

        return RunAsync(arguments, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(string id, CancellationToken cancellationToken)
    {
        if (Executable is not { } executable)
        {
            throw NotInstalled();
        }

        // `wsb connect` is the interactive client, not a setup command that exits after opening one.
        // Waiting for it would block target preparation until the user closed Sandbox. Start it as a
        // long-lived child; `wsb stop --id` remains the only lifecycle operation that ends it.
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            // ShellExecute does not inherit handles, so the client cannot hold the caller's captured
            // stdout/stderr open. That is load-bearing, not incidental: this child outlives winapp.
            // The suppression scope below keeps the guarantee even if this flag is ever changed.
            UseShellExecute = true,
            FileName = executable,
            CreateNoWindow = false,
        };
        foreach (var argument in (string[])["connect", "--id", id, "--raw"])
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (StandardHandleInheritance.Suppress())
        {
            ConnectLauncher(startInfo);
        }

        await Task.CompletedTask;
    }

    private static void LaunchConnectedClient(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Windows Sandbox client.");
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteAsync(
        string id,
        string command,
        string? workingDirectory,
        bool asSystem,
        CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "exec",
            "--id", id,
            "--command", command,
            "--run-as", asSystem ? "System" : "ExistingLogin",
            "--raw",
        ];

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            arguments.Add("--working-directory");
            arguments.Add(workingDirectory);
        }

        var result = await RunAsync(arguments, cancellationToken, throwOnFailure: false).ConfigureAwait(false);

        // wsb exec never relays the guest's stdout or stderr, so anything on stderr is wsb's own
        // diagnostic, meaning the command was not dispatched. Returning that exit code as if it came
        // from the guest process would let an infrastructure failure impersonate an application
        // result -- exactly the confusion the failure model exists to prevent.
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                $"The guest bootstrap command could not be dispatched: {Summarize(result)}",
                userAction: "Retry the command. If it keeps failing, close the Sandbox and try again.",
                context: new Dictionary<string, string>
                {
                    ["sandboxId"] = id,
                    ["exitCode"] = result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return result.ExitCode;
    }

    /// <inheritdoc/>
    public async Task LaunchAgentAsync(
        string id,
        string command,
        CancellationToken cancellationToken)
    {
        if (Executable is null)
        {
            throw NotInstalled();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // `wsb exec` hosting the persistent guest agent runs for the life of the Sandbox, which is
        // far longer than this command. It must therefore not inherit the caller's standard handles:
        // a caller capturing winapp's output would otherwise wait for end of stream until the whole
        // Sandbox went away.
        var launch = RunAsync(
            [
                "exec",
                "--id", id,
                "--command", command,
                "--run-as", "ExistingLogin",
                "--raw",
            ],
            CancellationToken.None,
            throwOnFailure: false,
            outlivesCaller: true);

        _ = ObserveAgentLaunchAsync(launch);

        var completed = await Task.WhenAny(
            launch,
            Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)).ConfigureAwait(false);

        if (completed == launch)
        {
            var result = await launch.ConfigureAwait(false);

            // An immediate WSB diagnostic means ExistingLogin was not ready and the command was
            // never dispatched. A guest exit with clean stderr is different: the agent publishes its
            // own staged heartbeat/log, which the backend reads as the authoritative diagnosis.
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TransportFailed,
                    $"The guest agent bootstrap could not be dispatched: {Summarize(result)}",
                    userAction: "Retry the command once the Sandbox login session is ready.",
                    context: new Dictionary<string, string>
                    {
                        ["sandboxId"] = id,
                        ["exitCode"] = result.ExitCode.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    });
            }
        }
    }

    private static async Task ObserveAgentLaunchAsync(Task<ProcessRunResult> launch)
    {
        try
        {
            var result = await launch.ConfigureAwait(false);
            if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "The persistent Windows Sandbox agent launch ended: {0}",
                    Summarize(result));
            }
        }
        catch (Exception ex) when (ex is ExecutionTargetException
                                   or IOException
                                   or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "The persistent Windows Sandbox agent launch failed: {0}",
                ex.Message);
        }
    }

    private async Task<ProcessRunResult> RunAsync(
        List<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true,
        bool outlivesCaller = false)
    {
        if (Executable is not { } executable)
        {
            throw NotInstalled();
        }

        var result = await processRunner
            .RunAsync(
                new ProcessRunRequest(executable, arguments) { OutlivesCaller = outlivesCaller },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (throwOnFailure && result.ExitCode != 0)
        {
            var verb = arguments.Count > 0 ? arguments[0] : string.Empty;
            var hresult = WsbHResult.Extract(result);

            var context = new Dictionary<string, string>
            {
                ["wsbVerb"] = verb,
                ["exitCode"] = result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            // The HRESULT is carried through rather than folded into the message, because two of
            // them change what the caller does next: CO_E_APPSINGLEUSE means an instance already
            // exists and should be reused, and ERROR_FILE_NOT_FOUND from `start` can accompany an
            // instance that was created anyway and has to be reconciled.
            if (hresult is { } code)
            {
                context[WsbHResult.ContextKey] = WsbHResult.Format(code);
            }

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StartFailed,
                $"The Windows Sandbox command line failed: {Summarize(result)}",
                userAction: "Retry the command. If it keeps failing, restart the host.",
                context: context);
        }

        return result;
    }

    /// <summary>The failure for a host where no trusted <c>wsb.exe</c> could be resolved.</summary>
    /// <remarks>
    /// Reached only when readiness setup did not run or did not finish, since a prepared target
    /// binds this client to the executable the probe validated. It deliberately offers no
    /// feature-enabling guidance: the setup runner classifies <em>why</em> the host is not ready and
    /// says so precisely, and repeating a guess here would contradict it.
    /// </remarks>
    private static ExecutionTargetException NotInstalled() =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.Unsupported,
            "The Windows Sandbox command line (wsb.exe) is not available on this host.",
            userAction: "Run the command again so winapp can finish setting up Windows Sandbox.",
            example: "winapp run . --sandbox");

    private static T? Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "The Windows Sandbox command line returned output winapp could not understand.",
                userAction: "Update Windows so wsb.exe matches a supported version, then retry.",
                innerException: ex);
        }
    }

    /// <summary>Trims wsb's own diagnostics to a single line for the failure message.</summary>
    private static string Summarize(ProcessRunResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;

        text = text?.Trim() ?? string.Empty;
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            text = text[..newline];
        }

        return string.IsNullOrEmpty(text)
            ? $"exit code {result.ExitCode}"
            : text;
    }

    /// <summary>
    /// Locates <c>wsb.exe</c> on PATH without launching it.
    /// </summary>
    /// <remarks>
    /// Relative PATH entries are skipped. A relative entry resolves against the current directory,
    /// so honouring one would reintroduce exactly the hijack that using an absolute path prevents.
    /// </remarks>
    internal static string? ResolveExecutable()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var trimmed = directory.Trim().Trim('"');
                if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed))
                {
                    continue;
                }

                // Route even this through the one managed-path invariant rather than combining
                // directly. The segment is a constant here, so the check can never fire -- but the
                // value of a central rule is that no call site is exempt from it, and a future edit
                // that made this segment dynamic would be validated automatically instead of
                // silently becoming the one place that was not.
                var candidate = Orchestration.TargetPathSafety.CombineInsideRoot(trimmed, ExecutableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not a reason to fail the probe.
            }
        }

        return null;
    }
}

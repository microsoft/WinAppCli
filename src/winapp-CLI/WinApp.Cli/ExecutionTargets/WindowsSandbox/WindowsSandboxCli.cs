// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
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
    /// <summary>Executable name; resolved through PATH like the rest of winapp's tool invocations.</summary>
    internal const string ExecutableName = "wsb.exe";

    private readonly Lazy<bool> _available = new(() => ResolveExecutable() is not null);

    /// <inheritdoc/>
    public bool IsAvailable => _available.Value;

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
    public async Task<string> StartAsync(string? configuration, CancellationToken cancellationToken)
    {
        List<string> arguments = ["start", "--raw"];
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            arguments.Add("--config");
            arguments.Add(configuration);
        }

        var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        var payload = Deserialize(result.StandardOutput, WindowsSandboxCliJsonContext.Default.WsbEnvironmentList);

        // Accept either shape: a bare root ID, or the same list wrapper `list` uses.
        var id = payload?.Id ?? payload?.WindowsSandboxEnvironments?.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StartFailed,
                "Windows Sandbox started but did not report an instance ID.",
                userAction: "Retry the command. If it keeps failing, restart the host.");
        }

        return id;
    }

    /// <inheritdoc/>
    public Task StopAsync(string id, CancellationToken cancellationToken) =>
        RunAsync(["stop", "--id", id, "--raw"], cancellationToken);

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
    public Task ConnectAsync(string id, CancellationToken cancellationToken) =>
        RunAsync(["connect", "--id", id, "--raw"], cancellationToken);

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
        return result.ExitCode;
    }

    private async Task<ProcessRunResult> RunAsync(
        List<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
        if (!IsAvailable)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                "The Windows Sandbox command line (wsb.exe) was not found.",
                userAction: "Install the Windows Sandbox optional feature, then retry.",
                example: "winapp run . --sandbox");
        }

        var result = await processRunner
            .RunAsync(new ProcessRunRequest(ExecutableName, arguments), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (throwOnFailure && result.ExitCode != 0)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StartFailed,
                $"The Windows Sandbox command line failed: {Summarize(result)}",
                userAction: "Retry the command. If it keeps failing, restart the host.",
                context: new Dictionary<string, string>
                {
                    ["wsbVerb"] = arguments.Count > 0 ? arguments[0] : string.Empty,
                    ["exitCode"] = result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return result;
    }

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

    /// <summary>Locates <c>wsb.exe</c> on PATH without launching it.</summary>
    private static string? ResolveExecutable()
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
                var candidate = Path.Combine(directory.Trim(), ExecutableName);
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

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

internal sealed class WindowsSandboxCli(IProcessRunner processRunner) : IWindowsSandboxCli
{
    private const string ExecutableName = "wsb.exe";

    public async Task<WindowsSandboxCliResult<IReadOnlyList<string>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var processResult = await RunAsync(["list", "--raw"], cancellationToken);
        if (!processResult.Succeeded)
        {
            return WindowsSandboxCliResult<IReadOnlyList<string>>.Failed(
                processResult.Failure!.Value,
                processResult.Error!);
        }

        try
        {
            var output = JsonSerializer.Deserialize(
                processResult.Value!.StandardOutput,
                WindowsSandboxCliJsonContext.Default.WindowsSandboxListOutput);
            if (output?.WindowsSandboxEnvironments is null)
            {
                return IncompatibleList("The WSB list response did not contain WindowsSandboxEnvironments.");
            }

            var ids = new List<string>(output.WindowsSandboxEnvironments.Count);
            foreach (var environment in output.WindowsSandboxEnvironments)
            {
                if (!TryNormalizeId(environment.Id, out var id))
                {
                    return IncompatibleList("The WSB list response contained an invalid sandbox ID.");
                }
                ids.Add(id);
            }

            if (ids.Count != ids.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                return IncompatibleList("The WSB list response contained duplicate sandbox IDs.");
            }

            return WindowsSandboxCliResult<IReadOnlyList<string>>.Success(ids);
        }
        catch (JsonException ex)
        {
            return IncompatibleList($"The WSB list response was not valid JSON: {ex.Message}");
        }
    }

    public async Task<WindowsSandboxCliResult<string>> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var processResult = await RunAsync(["start", "--raw"], cancellationToken);
        if (!processResult.Succeeded)
        {
            return WindowsSandboxCliResult<string>.Failed(
                processResult.Failure!.Value,
                processResult.Error!);
        }

        try
        {
            var output = JsonSerializer.Deserialize(
                processResult.Value!.StandardOutput,
                WindowsSandboxCliJsonContext.Default.WindowsSandboxStartOutput);
            if (output is null || !TryNormalizeId(output.Id, out var id))
            {
                return WindowsSandboxCliResult<string>.Failed(
                    WindowsSandboxCliFailure.IncompatibleOutput,
                    "The WSB start response did not contain a valid sandbox ID.");
            }

            return WindowsSandboxCliResult<string>.Success(id);
        }
        catch (JsonException ex)
        {
            return WindowsSandboxCliResult<string>.Failed(
                WindowsSandboxCliFailure.IncompatibleOutput,
                $"The WSB start response was not valid JSON: {ex.Message}");
        }
    }

    public async Task<WindowsSandboxCliResult<bool>> StopAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeId(instanceId, out var normalizedId))
        {
            throw new ArgumentException("Windows Sandbox instance IDs must be GUIDs.", nameof(instanceId));
        }

        var processResult = await RunAsync(
            ["stop", "--id", normalizedId, "--raw"],
            cancellationToken);
        if (!processResult.Succeeded)
        {
            return WindowsSandboxCliResult<bool>.Failed(
                processResult.Failure!.Value,
                processResult.Error!);
        }

        return WindowsSandboxCliResult<bool>.Success(true);
    }

    private async Task<WindowsSandboxCliResult<ProcessRunResult>> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync(
                new ProcessRunRequest(ExecutableName, arguments),
                cancellationToken: cancellationToken);
            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"wsb.exe exited with code {result.ExitCode}."
                    : $"wsb.exe exited with code {result.ExitCode}: {result.StandardError.Trim()}";
                return WindowsSandboxCliResult<ProcessRunResult>.Failed(
                    WindowsSandboxCliFailure.CommandFailed,
                    detail);
            }

            return WindowsSandboxCliResult<ProcessRunResult>.Success(result);
        }
        catch (Win32Exception ex)
        {
            return WindowsSandboxCliResult<ProcessRunResult>.Failed(
                WindowsSandboxCliFailure.ExecutableMissing,
                $"wsb.exe could not be started: {ex.Message}");
        }
    }

    private static bool TryNormalizeId(string? value, out string normalized)
    {
        if (Guid.TryParse(value, out var id))
        {
            normalized = id.ToString("D");
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static WindowsSandboxCliResult<IReadOnlyList<string>> IncompatibleList(string error) =>
        WindowsSandboxCliResult<IReadOnlyList<string>>.Failed(
            WindowsSandboxCliFailure.IncompatibleOutput,
            error);
}

internal sealed class WindowsSandboxListOutput
{
    public List<WindowsSandboxEnvironmentOutput>? WindowsSandboxEnvironments { get; set; }
}

internal sealed class WindowsSandboxEnvironmentOutput
{
    public string? Id { get; set; }
}

internal sealed class WindowsSandboxStartOutput
{
    public string? Id { get; set; }
}

[JsonSerializable(typeof(WindowsSandboxListOutput))]
[JsonSerializable(typeof(WindowsSandboxStartOutput))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
internal partial class WindowsSandboxCliJsonContext : JsonSerializerContext;

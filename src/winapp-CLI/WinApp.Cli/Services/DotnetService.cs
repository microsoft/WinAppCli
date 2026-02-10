// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal class DotnetService : IDotnetService
{
    public async Task<bool> IsDotnetInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, _) = await RunDotnetProcessAsync("--version", cancellationToken);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsWinUITemplatesInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, output) = await RunDotnetProcessAsync("new list --author \"Vijay Anand\"", cancellationToken);
            return exitCode == 0 && output.Contains("winui", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<(int exitCode, string output)> InstallWinUITemplatesAsync(
        TaskContext taskContext, CancellationToken cancellationToken)
    {
        taskContext.AddStatusMessage("Installing WinUI 3 templates (VijayAnand.WinUITemplates)...");
        var result = await RunDotnetProcessAsync("new install VijayAnand.WinUITemplates", cancellationToken);

        if (result.exitCode == 0)
        {
            taskContext.AddStatusMessage("WinUI 3 templates installed successfully.");
        }

        return result;
    }

    public async Task<(int exitCode, string output)> RunDotnetNewAsync(
        string templateShortName, string? name, string? outputDir,
        Dictionary<string, string>? parameters,
        TaskContext taskContext, CancellationToken cancellationToken)
    {
        var args = new StringBuilder($"new {templateShortName}");

        if (!string.IsNullOrEmpty(name))
        {
            args.Append($" -n \"{name}\"");
        }

        if (!string.IsNullOrEmpty(outputDir))
        {
            args.Append($" -o \"{outputDir}\"");
        }

        if (parameters != null)
        {
            foreach (var (key, value) in parameters)
            {
                args.Append($" --{key} {value}");
            }
        }

        taskContext.AddStatusMessage($"Running: dotnet {args}");
        return await RunDotnetProcessAsync(args.ToString(), cancellationToken);
    }

    public async Task<(int exitCode, string output)> RunDotnetRestoreAsync(
        string projectDir, TaskContext taskContext, CancellationToken cancellationToken)
    {
        taskContext.AddStatusMessage("Restoring project packages...");
        return await RunDotnetProcessAsync($"restore \"{projectDir}\"", cancellationToken);
    }

    private static async Task<(int exitCode, string output)> RunDotnetProcessAsync(
        string arguments, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        if (process == null)
        {
            return (1, "Failed to start dotnet process.");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (process.ExitCode, output.Trim());
    }
}

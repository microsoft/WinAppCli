// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;

namespace WinApp.Cli.Services;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = request.CreateNoWindow,
        };

        // Use ArgumentList rather than a concatenated string so already-validated values can never
        // be reinterpreted as additional command-line arguments to the target.
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (request.Environment != null)
        {
            foreach (var kvp in request.Environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{request.FileName}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = DrainAsync(process.StandardOutput, stdout, onOutputLine, cancellationToken);
        var stderrTask = DrainAsync(process.StandardError, stderr, onErrorLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine, CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            sink.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup on cancellation.
        }
    }
}

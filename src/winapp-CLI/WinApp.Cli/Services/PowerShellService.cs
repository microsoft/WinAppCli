// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for executing PowerShell commands
/// </summary>
internal class PowerShellService : IPowerShellService
{
    /// <summary>
    /// Runs a PowerShell command and returns the exit code and output
    /// </summary>
    /// <param name="command">The PowerShell command to run</param>
    /// <param name="elevated">Whether to run with elevated privileges (UAC prompt)</param>
    /// <param name="environmentVariables">Optional dictionary of environment variables to set/override</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing (exitCode, stdout, stderr)</returns>
    public async Task<(int exitCode, string output, string error)> RunCommandAsync(
        string command,
        TaskContext taskContext,
        bool elevated = false,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var elevatedText = elevated ? "elevated " : "";
        taskContext.AddDebugMessage($"Running {elevatedText}PowerShell: {command}");
        if (elevated)
        {
            taskContext.AddDebugMessage("UAC prompt may appear...");
        }

        // Build a safe, profile-less, non-interactive PowerShell invocation
        // Prefix command to suppress progress records that can otherwise pollute stderr output.
        var preparedCommand = $"$ProgressPreference='SilentlyContinue'; {command}";

        static string ToEncodedCommand(string s)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(s); // UTF-16LE
            return Convert.ToBase64String(bytes);
        }
        var encoded = ToEncodedCommand(preparedCommand);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = elevated, // Required for elevation, must be true for Verb=runas
            RedirectStandardOutput = !elevated, // Always redirect when not elevated so we can capture output
            RedirectStandardError = !elevated,
            RedirectStandardInput = !elevated, // close stdin so PS never waits for input
            CreateNoWindow = !elevated,
            WindowStyle = elevated ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };

        // Apply custom environment variables if provided (only when not elevated)
        // When elevated, UseShellExecute=true which doesn't support environment variables
        if (!elevated)
        {
            if (environmentVariables is not null)
            {
                foreach (var kvp in environmentVariables)
                {
                    psi.Environment[kvp.Key] = kvp.Value;
                }
            }

            // Always clear PSModulePath to prevent PowerShell Core module conflicts when calling Windows PowerShell
            // This fixes the issue where calling powershell.exe from PowerShell Core causes module loading errors
            if (!psi.Environment.ContainsKey("PSModulePath"))
            {
                psi.Environment["PSModulePath"] = "";
            }
        }

        if (elevated)
        {
            psi.Verb = "runas"; // This triggers UAC elevation
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            return (-1, string.Empty, "Failed to start PowerShell process");
        }

        string stdOut = string.Empty, stdErr = string.Empty;

        if (!elevated)
        {
            // Read both streams concurrently to avoid deadlocks
            var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Close stdin immediately; we won’t provide input
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            await Task.WhenAll(outTask, errTask);
            stdOut = NormalizePowerShellStream(outTask.Result);
            stdErr = NormalizePowerShellStream(errTask.Result);
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stdErr))
        {
            taskContext.AddDebugMessage($"PowerShell error: {Environment.NewLine}{stdErr.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(stdOut))
        {
            taskContext.AddDebugMessage($"PowerShell output: {Environment.NewLine}{stdOut.Trim().TrimStart(Environment.NewLine).TrimEnd(Environment.NewLine)}");
        }

        // For elevated commands, exit codes may not be reliable, so we return 0 if no exception occurred
        var exitCode = elevated ? (process.ExitCode == 0 ? 0 : process.ExitCode) : process.ExitCode;

        return (exitCode, stdOut, stdErr);
    }

    private static string NormalizePowerShellStream(string stream)
    {
        if (string.IsNullOrWhiteSpace(stream))
        {
            return string.Empty;
        }

        var trimmed = stream.Trim();
        var reasonIndex = trimmed.IndexOf("Reason:", StringComparison.OrdinalIgnoreCase);
        if (reasonIndex < 0)
        {
            return string.Empty;
        }

        var reasonStart = reasonIndex + "Reason:".Length;
        var section = trimmed[reasonStart..];

        var noteIndex = section.IndexOf("NOTE:", StringComparison.OrdinalIgnoreCase);
        if (noteIndex >= 0)
        {
            section = section[..noteIndex];
        }

        section = StripXmlTags(section);
        section = DecodeClixmlEscapes(section);
        section = section.Replace("#< CLIXML", string.Empty, StringComparison.OrdinalIgnoreCase);

        var lines = section
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join(" ", lines).Trim();
    }

    private static string StripXmlTags(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        var inTag = false;

        foreach (var character in value)
        {
            if (character == '<')
            {
                inTag = true;
                continue;
            }

            if (character == '>')
            {
                inTag = false;
                sb.Append(' ');
                continue;
            }

            if (!inTag)
            {
                sb.Append(character);
            }
        }

        return sb.ToString();
    }

    private static string DecodeClixmlEscapes(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (i + 6 < value.Length
                && value[i] == '_'
                && (value[i + 1] == 'x' || value[i + 1] == 'X')
                && IsHex(value[i + 2])
                && IsHex(value[i + 3])
                && IsHex(value[i + 4])
                && IsHex(value[i + 5])
                && value[i + 6] == '_')
            {
                var hex = value.AsSpan(i + 2, 4);
                var codePoint = Convert.ToInt32(hex.ToString(), 16);

                sb.Append(codePoint switch
                {
                    0x000D => '\r',
                    0x000A => '\n',
                    0x0009 => ' ',
                    _ => (char)codePoint
                });

                i += 6;
                continue;
            }

            sb.Append(value[i]);
        }

        return sb.ToString();

        static bool IsHex(char c)
            => (c >= '0' && c <= '9')
               || (c >= 'a' && c <= 'f')
               || (c >= 'A' && c <= 'F');
    }
}

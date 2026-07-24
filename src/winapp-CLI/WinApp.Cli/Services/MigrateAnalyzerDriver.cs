// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="IMigrateAnalyzerDriver"/>
internal sealed class MigrateAnalyzerDriver : IMigrateAnalyzerDriver
{
    private const string EnvOverride = "WINAPP_MIGRATE_ANALYZER";

    public string? ResolveDriverPath()
    {
        var env = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var exeName = OperatingSystem.IsWindows() ? "winui-analyze.exe" : "winui-analyze";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", exeName),
            Path.Combine(AppContext.BaseDirectory, exeName),
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    public async Task<MigrateAnalyzerRun> RunAsync(DirectoryInfo directory, FileInfo? project, bool fromUwp, CancellationToken cancellationToken)
    {
        var driver = ResolveDriverPath();
        if (driver is null)
        {
            return new MigrateAnalyzerRun(DriverFound: false, ExitCode: -1, StdOut: string.Empty, StdErr: string.Empty);
        }

        var psi = new ProcessStartInfo
        {
            FileName = driver,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(directory.FullName);
        if (fromUwp)
        {
            psi.ArgumentList.Add("--from-uwp");
        }
        if (project is not null)
        {
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(project.FullName);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new MigrateAnalyzerRun(DriverFound: true, process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

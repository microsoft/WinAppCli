// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake analyzer driver that records calls and returns a canned <see cref="MigrateAnalyzerRun"/>
/// without shelling out to the real out-of-process winui-analyze exe.
/// </summary>
internal sealed class FakeMigrateAnalyzerDriver : IMigrateAnalyzerDriver
{
    public bool DriverFound { get; set; } = true;
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = """{"schemaVersion":"1.0","files":[]}""";
    public string StdErr { get; set; } = "";
    public string? DriverPath { get; set; } = @"C:\fake\tools\winui-analyze.exe";
    public Exception? ThrowOnRun { get; set; }

    public List<(string Directory, string? Project, bool FromUwp)> Runs { get; } = [];

    public string? ResolveDriverPath() => DriverPath;

    public Task<MigrateAnalyzerRun> RunAsync(DirectoryInfo directory, FileInfo? project, bool fromUwp, CancellationToken cancellationToken)
    {
        Runs.Add((directory.FullName, project?.FullName, fromUwp));
        if (ThrowOnRun is not null)
        {
            throw ThrowOnRun;
        }

        return Task.FromResult(new MigrateAnalyzerRun(DriverFound, ExitCode, StdOut, StdErr));
    }
}

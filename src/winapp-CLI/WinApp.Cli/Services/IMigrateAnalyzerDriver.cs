// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>Result of shelling out to the bundled winui-analyze driver exe.</summary>
internal sealed record MigrateAnalyzerRun(bool DriverFound, int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Resolves and runs the out-of-process analyzer driver (winui-analyze) that backs
/// `winapp migrate analyze` / `validate`. winapp ships as NativeAOT so the Roslyn analyzer
/// cannot run in-process; it is bundled as a self-contained exe under the CLI tools folder.
/// </summary>
internal interface IMigrateAnalyzerDriver
{
    /// <summary>Full path to the driver exe, or null if it cannot be located.</summary>
    string? ResolveDriverPath();

    /// <summary>Runs the driver over <paramref name="directory"/> and captures stdout/stderr.</summary>
    Task<MigrateAnalyzerRun> RunAsync(DirectoryInfo directory, FileInfo? project, bool fromUwp, CancellationToken cancellationToken);
}

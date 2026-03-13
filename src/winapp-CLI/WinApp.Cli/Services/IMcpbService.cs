// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for converting MCP Bundle (.mcpb) files into an MSIX-ready staging directory.
/// </summary>
internal interface IMcpbService
{
    /// <summary>
    /// Extracts an MCP Bundle, validates the manifest, generates an AppxManifest.xml,
    /// and stages all files into a directory ready for MSIX packaging.
    /// </summary>
    /// <param name="mcpbPath">Path to the .mcpb file</param>
    /// <param name="architecture">Target processor architecture (x64, x86, arm64)</param>
    /// <param name="publisher">Certificate publisher subject (e.g., CN=Publisher)</param>
    /// <param name="runtimePath">Optional path to a runtime executable for script-based servers</param>
    /// <param name="taskContext">Task context for status/debug messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the staging directory path and metadata</returns>
    Task<McpbConversionResult> ExtractAndPrepareAsync(
        FileInfo mcpbPath,
        string architecture,
        string publisher,
        string? runtimePath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);
}

internal sealed record McpbConversionResult(
    DirectoryInfo StagingDirectory,
    string PackageName,
    string PackageVersion,
    string DisplayName,
    string EntryPointExe);

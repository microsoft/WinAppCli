// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Single owner of the JS-bindings pipeline; invoked from init/restore Step 5.5
// when winapp.yaml declares a jsBindings: block.
internal interface IJsBindingsWorkspaceService
{
    // discover → partition → resolve user winmds → codegen → ensure runtime dep.
    Task<JsBindingsOrchestrationResult> RunAsync(
        JsBindingsOrchestrationContext context,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    // Inject @microsoft/dynwinrt into package.json as a production dep, then
    // print a package-manager-aware install hint. Called early in init when
    // the npm-caller prompt opted into JS bindings so users can `npm install`
    // while codegen runs.
    void EnsureRuntimeDependencyAndPrintHint(DirectoryInfo workspaceDirectory);
}

// Inputs to IJsBindingsWorkspaceService.RunAsync.
internal sealed class JsBindingsOrchestrationContext
{
    public required JsBindingsConfig JsBindingsConfig { get; init; }
    public required WinappConfig WinappConfig { get; init; }
    public required DirectoryInfo WorkspaceDir { get; init; }
    public required DirectoryInfo LocalWinappDir { get; init; }
    public required DirectoryInfo NugetCacheDir { get; init; }

    // (name → version) incl. transitive deps. Populated by the init / restore
    // flow before invoking RunAsync. Null forces the lockfile fast-path or
    // live transitive expansion (used by tests and future external callers).
    public IReadOnlyDictionary<string, string>? UsedVersions { get; init; }

    public bool EnsureRuntimeDependency { get; init; } = true;
}

internal sealed class JsBindingsOrchestrationResult
{
    public required int ExitCode { get; init; }
    public required string Message { get; init; }
    public DirectoryInfo? OutputDir { get; init; }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Single owner of the JS-bindings pipeline; used by both init/restore and add.
internal interface IJsBindingsWorkspaceService
{
    // discover → partition → resolve user winmds → codegen → ensure runtime dep.
    Task<JsBindingsOrchestrationResult> RunAsync(
        JsBindingsOrchestrationContext context,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    // Inject @microsoft/dynwinrt into package.json as a production dep, then
    // print a package-manager-aware install hint. Called early in init when
    // --js-bindings is set so users can `npm install` while codegen runs.
    void EnsureRuntimeDependencyAndPrintHint(DirectoryInfo workspaceDirectory);

    // Top-level `node jsbindings add` flow: load winapp.yaml, prompt about
    // existing block, splice-save, invoke RunAsync, cleanup old output dir.
    // Returns the exit code suitable for the System.CommandLine handler.
    Task<int> AddAsync(AddJsBindingsOptions options, CancellationToken cancellationToken = default);

    // Top-level `node jsbindings generate` flow: read existing jsBindings:
    // block from winapp.yaml without mutation, then run codegen. Errors if
    // no jsBindings: block exists.
    Task<int> GenerateAsync(GenerateJsBindingsOptions options, CancellationToken cancellationToken = default);
}

// Inputs to IJsBindingsWorkspaceService.RunAsync.
internal sealed class JsBindingsOrchestrationContext
{
    public required JsBindingsConfig JsBindingsConfig { get; init; }
    public required WinappConfig WinappConfig { get; init; }
    public required DirectoryInfo WorkspaceDir { get; init; }
    public required DirectoryInfo LocalWinappDir { get; init; }
    public required DirectoryInfo NugetCacheDir { get; init; }

    // (name → version) incl. transitive deps. null on the add path — derived
    // from lockfile / transitive expansion.
    public IReadOnlyDictionary<string, string>? UsedVersions { get; init; }

    public bool EnsureRuntimeDependency { get; init; } = true;
}

internal sealed class JsBindingsOrchestrationResult
{
    public required int ExitCode { get; init; }
    public required string Message { get; init; }
    public DirectoryInfo? OutputDir { get; init; }
}

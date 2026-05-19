// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests.TestDoubles;

// Recording fake for the init/restore JS-bindings exec path. Lets tests
// drive WorkspaceSetupService → JsBindingsWorkspaceService.RunAsync without
// spawning real codegen or seeding NuGet caches.
internal sealed class FakeJsBindingsWorkspaceService : IJsBindingsWorkspaceService
{
    public List<JsBindingsOrchestrationContext> Calls { get; } = new();

    // When set, RunAsync returns this result instead of a default success.
    public JsBindingsOrchestrationResult? Result { get; set; }

    public Task<JsBindingsOrchestrationResult> RunAsync(
        JsBindingsOrchestrationContext context,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(context);
        return Task.FromResult(Result ?? new JsBindingsOrchestrationResult
        {
            ExitCode = 0,
            Message = "fake codegen success",
            OutputDir = context.WorkspaceDir,
        });
    }

    public bool EnsureRuntimeDependencyCalled { get; private set; }
    public void EnsureRuntimeDependencyAndPrintHint(DirectoryInfo workspaceDirectory)
    {
        EnsureRuntimeDependencyCalled = true;
    }

    // AddAsync isn't exercised by the init/restore wiring tests (those go
    // through RunAsync). Stub returns success so the interface is satisfied.
    public Task<int> AddAsync(AddJsBindingsOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public List<GenerateJsBindingsOptions> GenerateCalls { get; } = new();

    // When set, GenerateAsync returns this exit code instead of 0.
    public int GenerateResult { get; set; }

    public Task<int> GenerateAsync(GenerateJsBindingsOptions options, CancellationToken cancellationToken = default)
    {
        GenerateCalls.Add(options);
        return Task.FromResult(GenerateResult);
    }
}

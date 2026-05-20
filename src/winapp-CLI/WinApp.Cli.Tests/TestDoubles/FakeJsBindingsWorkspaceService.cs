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
}

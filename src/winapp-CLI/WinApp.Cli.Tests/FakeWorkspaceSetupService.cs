// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake workspace-setup service that records calls and returns configurable results
/// without touching NuGet, the filesystem, or build tools.
/// </summary>
internal sealed class FakeWorkspaceSetupService : IWorkspaceSetupService
{
    public List<WorkspaceSetupOptions> SetupWorkspaceCalls { get; } = [];

    /// <summary>Exit code returned by <see cref="SetupWorkspaceAsync"/>.</summary>
    public int SetupWorkspaceResult { get; set; }

    public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
    {
        SetupWorkspaceCalls.Add(options);
        return Task.FromResult(SetupWorkspaceResult);
    }
}

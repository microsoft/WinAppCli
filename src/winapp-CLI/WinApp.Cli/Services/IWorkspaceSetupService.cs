// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IWorkspaceSetupService
{
    public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default);
}

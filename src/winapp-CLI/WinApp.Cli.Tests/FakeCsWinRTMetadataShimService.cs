// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake <see cref="ICsWinRTMetadataShimService"/> for ProjectRunService tests. Returns
/// <see cref="FolderToReturn"/> (default <c>null</c> → no injection) and records the moniker it was asked
/// about so tests can assert the shim was consulted with the right target framework.
/// </summary>
internal sealed class FakeCsWinRTMetadataShimService : ICsWinRTMetadataShimService
{
    public string? FolderToReturn { get; set; }

    public List<string?> ResolvedMonikers { get; } = [];

    public string? ResolveMetadataFolder(string? targetFrameworkMoniker)
    {
        ResolvedMonikers.Add(targetFrameworkMoniker);
        return FolderToReturn;
    }
}

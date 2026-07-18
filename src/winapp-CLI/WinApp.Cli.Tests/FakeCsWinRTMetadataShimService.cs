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

    /// <summary>
    /// Optional per-call return values for <see cref="ResolveMetadataFolder"/>. When non-empty each call
    /// dequeues the next value (modelling the C1 restore-then-retry: first call <c>null</c> because the
    /// ref pack isn't restored yet, second call a folder once restore populated it). When empty,
    /// <see cref="FolderToReturn"/> is used for every call.
    /// </summary>
    public Queue<string?> FolderSequence { get; } = new();

    /// <summary>
    /// What <see cref="IsWindowsSdkAbsent"/> reports. Default <c>false</c> so existing tests keep the
    /// pre-shim restore path dormant unless they opt in.
    /// </summary>
    public bool WindowsSdkAbsent { get; set; }

    public List<string?> ResolvedMonikers { get; } = [];

    /// <summary>Number of times <see cref="IsWindowsSdkAbsent"/> was consulted.</summary>
    public int IsWindowsSdkAbsentCalls { get; private set; }

    public string? ResolveMetadataFolder(string? targetFrameworkMoniker)
    {
        ResolvedMonikers.Add(targetFrameworkMoniker);
        return FolderSequence.Count > 0 ? FolderSequence.Dequeue() : FolderToReturn;
    }

    public bool IsWindowsSdkAbsent()
    {
        IsWindowsSdkAbsentCalls++;
        return WindowsSdkAbsent;
    }
}

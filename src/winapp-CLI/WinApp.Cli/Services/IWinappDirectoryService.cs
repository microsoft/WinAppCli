// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Interface for resolving .winapp directory paths
/// </summary>
internal interface IWinappDirectoryService
{
    DirectoryInfo GetGlobalWinappDirectory();
    DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null);
    void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IPackageLayoutService
{
    public void CopyIncludesFromPackages(DirectoryInfo nugetCacheDir, DirectoryInfo includeOut, Dictionary<string, string> usedVersions);
    public void CopyLibsAllArch(DirectoryInfo nugetCacheDir, DirectoryInfo libRoot, Dictionary<string, string> usedVersions);
    public void CopyRuntimesAllArch(DirectoryInfo nugetCacheDir, DirectoryInfo binRoot, Dictionary<string, string> usedVersions);
    public IEnumerable<FileInfo> FindWinmds(DirectoryInfo nugetCacheDir, Dictionary<string, string> usedVersions);
}

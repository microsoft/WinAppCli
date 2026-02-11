// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Root model for the JSON output of `dotnet list package --format json`.
/// </summary>
public record DotNetPackageListJson(List<DotNetProject> Projects);

public record DotNetProject(List<DotNetFramework> Frameworks);

public record DotNetFramework(string Framework, List<DotNetPackage> TopLevelPackages, List<DotNetPackage> TransitivePackages);

public record DotNetPackage(string Id, string RequestedVersion, string ResolvedVersion);

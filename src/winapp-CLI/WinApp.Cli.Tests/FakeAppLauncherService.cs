// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake app launcher service that records launch calls without actually launching applications.
/// </summary>
internal class FakeAppLauncherService : IAppLauncherService
{
    public List<(string Aumid, string? Arguments)> LaunchCalls { get; } = [];
    public List<(string? PackageFullName, uint ProcessId)> TerminateCalls { get; } = [];
    public uint FakeProcessId { get; set; } = 12345;
    public string? FakePackageFullName { get; set; } = "FakePackage_1.0.0.0_x64__fakefamily";

    public uint LaunchByAumid(string aumid, string? arguments = null)
    {
        LaunchCalls.Add((aumid, arguments));
        return FakeProcessId;
    }

    public string ComputePackageFamilyName(string packageName, string publisher)
    {
        return $"{packageName}_fakefamily";
    }

    public string? GetPackageFullName(string packageFamilyName)
    {
        return FakePackageFullName;
    }

    public void TerminatePackageProcesses(string? packageFullName, uint processId)
    {
        TerminateCalls.Add((packageFullName, processId));
    }
}

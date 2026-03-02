// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake app launcher service that records launch calls without actually launching applications.
/// </summary>
internal class FakeAppLauncherService : IAppLauncherService
{
    public List<(string Aumid, string? Arguments)> LaunchCalls { get; } = [];
    public List<(string AliasName, string? Arguments)> AliasLaunchCalls { get; } = [];
    public List<string> EnableDebuggingCalls { get; } = [];
    public List<string> DisableDebuggingCalls { get; } = [];
    public uint FakeProcessId { get; set; } = 12345;

    public uint LaunchByAumid(string aumid, string? arguments = null)
    {
        LaunchCalls.Add((aumid, arguments));
        return FakeProcessId;
    }

    public Process LaunchByAlias(string aliasName, string? arguments = null)
    {
        AliasLaunchCalls.Add((aliasName, arguments));
        // Return the current process as a stand-in for tests
        return Process.GetCurrentProcess();
    }

    public string ComputePackageFamilyName(string packageName, string publisher)
    {
        return $"{packageName}_fakefamily";
    }

    public void EnablePackageDebugging(string packageFullName)
    {
        EnableDebuggingCalls.Add(packageFullName);
    }

    public void DisablePackageDebugging(string packageFullName)
    {
        DisableDebuggingCalls.Add(packageFullName);
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Provides methods for launching packaged Windows applications and computing
/// MSIX package identity values.
/// </summary>
internal interface IAppLauncherService
{
    /// <summary>
    /// Launches a packaged application by its Application User Model ID (AUMID).
    /// </summary>
    /// <param name="aumid">The Application User Model ID (e.g. <c>PackageFamilyName!App</c>).</param>
    /// <param name="arguments">Optional command-line arguments to pass to the application.</param>
    /// <returns>The process ID of the launched application.</returns>
    uint LaunchByAumid(string aumid, string? arguments = null);

    /// <summary>
    /// Computes the package family name from a package name and publisher distinguished name.
    /// The result follows the Windows format: <c>{packageName}_{publisherId}</c>, where the
    /// publisher ID is a 13-character Crockford Base32 encoding derived from the publisher's SHA256 hash.
    /// </summary>
    /// <param name="packageName">The MSIX package name (e.g. <c>MyCompany.MyApp</c>).</param>
    /// <param name="publisher">The publisher distinguished name (e.g. <c>CN=MyCompany</c>).</param>
    /// <returns>The computed package family name.</returns>
    string ComputePackageFamilyName(string packageName, string publisher);
}

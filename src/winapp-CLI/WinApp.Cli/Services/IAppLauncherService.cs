// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Controls how a directly-launched child process's standard streams are wired up.
/// </summary>
internal enum LaunchStdioMode
{
    /// <summary>
    /// The child inherits winapp's stdin/stdout/stderr so console/UI output streams inline,
    /// matching <c>dotnet run</c>. Correct for a foreground, non-JSON launch.
    /// </summary>
    Inherit,

    /// <summary>
    /// The child's standard streams are redirected away from winapp's own handles and drained to
    /// nothing. Required for <c>--detach</c> and <c>--json</c>: it stops the child from holding the
    /// parent's captured stdout pipe open (so a detached launch returns promptly instead of blocking
    /// the npm wrapper until the app exits) and keeps winapp's <c>--json</c> stdout free of app output.
    /// </summary>
    Suppress,
}

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
    /// Launches an executable directly as a child process. Used for unpackaged (project-mode)
    /// WinUI apps, where there is no MSIX identity and the app is started via its apphost
    /// <c>.exe</c> (the evaluated MSBuild <c>RunCommand</c>).
    /// </summary>
    /// <param name="exePath">Absolute path to the runnable apphost <c>.exe</c>.</param>
    /// <param name="arguments">Optional command-line arguments to forward to the application.</param>
    /// <param name="workingDirectory">Working directory for the process (typically the output dir), or <c>null</c> to inherit.</param>
    /// <param name="stdioMode">
    /// How to wire the child's standard streams. <see cref="LaunchStdioMode.Inherit"/> (default) streams
    /// output inline for a foreground launch; <see cref="LaunchStdioMode.Suppress"/> decouples the child's
    /// streams for <c>--detach</c>/<c>--json</c> so it neither holds the parent capture pipe open nor
    /// corrupts JSON stdout.
    /// </param>
    /// <returns>
    /// An owned <see cref="ILaunchedProcess"/> handle. The caller must dispose it; keeping the handle
    /// (rather than the bare PID) preserves the exit code and prevents PID reuse while waiting.
    /// </returns>
    ILaunchedProcess LaunchExecutable(string exePath, string? arguments = null, string? workingDirectory = null, LaunchStdioMode stdioMode = LaunchStdioMode.Inherit);

    /// <summary>
    /// Terminates all processes belonging to a packaged application using
    /// <c>IPackageDebugSettings.TerminateAllProcesses</c>. Falls back to killing a
    /// single process by PID when the package-level termination fails.
    /// </summary>
    /// <param name="packageFullName">The full name of the package whose processes should be terminated, or <c>null</c> to skip package-level termination.</param>
    /// <param name="processId">Fallback process ID to kill if package-level termination fails or <paramref name="packageFullName"/> is <c>null</c>.</param>
    void TerminatePackageProcesses(string? packageFullName, uint processId);

    /// <summary>
    /// Computes the package family name from a package name and publisher distinguished name.
    /// The result follows the Windows format: <c>{packageName}_{publisherId}</c>, where the
    /// publisher ID is a 13-character Crockford Base32 encoding derived from the publisher's SHA256 hash.
    /// </summary>
    /// <param name="packageName">The MSIX package name (e.g. <c>MyCompany.MyApp</c>).</param>
    /// <param name="publisher">The publisher distinguished name (e.g. <c>CN=MyCompany</c>).</param>
    /// <returns>The computed package family name.</returns>
    string ComputePackageFamilyName(string packageName, string publisher);

    /// <summary>
    /// Resolves the package full name from a package family name by querying
    /// the system's package inventory.
    /// </summary>
    /// <param name="packageFamilyName">The package family name (e.g. <c>MyApp_abc123def</c>).</param>
    /// <returns>The package full name, or <c>null</c> if the package is not installed.</returns>
    string? GetPackageFullName(string packageFamilyName);
}

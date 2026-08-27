// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using Windows.Management.Deployment;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Measures what this host can currently do with Windows Sandbox.</summary>
internal interface IWindowsSandboxHostProbe
{
    /// <summary>Gathers the observed facts a setup decision is made from.</summary>
    Task<WindowsSandboxHostFacts> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads Windows Sandbox host state without elevation and without changing anything.
/// </summary>
/// <remarks>
/// <para>
/// Three independent signals are collected, because no single one distinguishes the states that
/// need different fixes. The System32 payload says whether the optional feature is enabled; the
/// package query says whether the Store-delivered client has been delivered and is healthy; and
/// <c>wsb.exe --version</c> says whether the two are actually joined up and usable. Only the last
/// is proof.
/// </para>
/// <para>
/// Every probe is deliberately non-elevated. <c>dism /Online /Get-FeatureInfo</c> and
/// <c>Get-WindowsOptionalFeature -Online</c> both fail with <c>ERROR_ELEVATION_REQUIRED</c> (740)
/// for an ordinary user, so a readiness check built on either would raise a UAC prompt merely to
/// look — which is why feature state is inferred from the payload file the feature writes rather
/// than from a servicing query.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxHostProbe(IProcessRunner processRunner) : IWindowsSandboxHostProbe
{
    /// <summary>How long <c>wsb.exe --version</c> gets before it is treated as unusable.</summary>
    /// <remarks>
    /// An alias whose package is mid-servicing can hang rather than fail. Bounding it keeps a probe
    /// that exists to answer "is this usable right now" from becoming the thing that blocks.
    /// </remarks>
    internal static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Existence check seam, so path handling can be exercised without those files.</summary>
    internal Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>Alias resolution seam.</summary>
    internal Func<string?> ResolveAlias { get; set; } = ResolveTrustedAlias;

    /// <summary>Package query seam; the real one calls <see cref="PackageManager"/>.</summary>
    internal Func<SandboxPackagePresence> QueryPackage { get; set; } = DefaultQueryPackage;

    /// <summary>Whether this host is Windows; a seam only so the non-Windows case is testable.</summary>
    internal Func<bool> IsWindows { get; set; } = OperatingSystem.IsWindows;

    /// <inheritdoc/>
    public async Task<WindowsSandboxHostFacts> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!IsWindows())
        {
            return new WindowsSandboxHostFacts
            {
                IsWindows = false,
                FeaturePayloadPresent = false,
                PackageRegistered = false,
                AliasPresent = false,
            };
        }

        var payloadPresent = FileExists(PayloadExecutablePath());
        var package = QueryPackage();
        var alias = ResolveAlias();

        var version = alias is null
            ? null
            : await TryReadVersionAsync(alias, cancellationToken).ConfigureAwait(false);

        return new WindowsSandboxHostFacts
        {
            IsWindows = true,
            FeaturePayloadPresent = payloadPresent,
            PackageRegistered = package.Registered,
            PackageStatus = package.Status,
            AliasPresent = alias is not null,
            ExecutablePath = alias,
            Version = version,
            Detail = package.Detail,
        };
    }

    /// <summary>Absolute path of the feature payload, which is also the client bootstrapper.</summary>
    /// <remarks>
    /// Built from <see cref="Environment.SystemDirectory"/> rather than from <c>%SystemRoot%</c>, so
    /// an environment variable cannot redirect either the readiness check or the process winapp
    /// later launches to make the client initialize.
    /// </remarks>
    internal static string PayloadExecutablePath() =>
        TargetPathSafety.CombineInsideRoot(
            Environment.SystemDirectory,
            WindowsSandboxReadiness.PayloadExecutableName);

    /// <summary>
    /// Finds a <c>wsb.exe</c> that is safe to execute, preferring PATH and falling back to the
    /// known WindowsApps folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback exists because a process launched with a stripped or minimal PATH — a build
    /// agent, a service, an editor task runner — sees no WindowsApps entry at all, even on a host
    /// where Sandbox is perfectly usable. Refusing to look anywhere else would report that host as
    /// unsupported.
    /// </para>
    /// <para>
    /// It is not a general search path, because that would reintroduce exactly the ambiguity
    /// absolute resolution exists to prevent. The candidate is built from the <em>known folder</em>
    /// for local application data (<c>SHGetFolderPath</c>) rather than from <c>%LOCALAPPDATA%</c>,
    /// and it is accepted only if it carries the reparse attribute every real execution alias has,
    /// so a plain executable dropped into that folder is not used.
    /// </para>
    /// <para>
    /// Both checks are robustness, not a security boundary. That folder is writable by the very
    /// user winapp runs as, and so is the shell-folder registry value behind the known-folder
    /// lookup — an attacker who can subvert either is already running as this user and could
    /// replace <c>winapp.exe</c> itself. What the checks do buy is that an ordinary stray file, or
    /// a redefined environment variable, does not silently become the Sandbox control plane.
    /// </para>
    /// </remarks>
    internal static string? ResolveTrustedAlias()
    {
        if (WindowsSandboxCli.ResolveExecutable() is { } onPath)
        {
            return onPath;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);

            if (string.IsNullOrEmpty(localAppData))
            {
                return null;
            }

            var candidate = TargetPathSafety.CombineInsideRoot(
                localAppData,
                "Microsoft",
                "WindowsApps",
                WindowsSandboxCli.ExecutableName);

            return IsExecutionAlias(candidate) ? candidate : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException
              or Abstractions.ExecutionTargetException)
        {
            return null;
        }
    }

    /// <summary>Whether a path is an app execution alias rather than an ordinary executable.</summary>
    private static bool IsExecutionAlias(string path)
    {
        try
        {
            var info = new FileInfo(path);

            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Runs <c>wsb.exe --version</c> and returns what it printed, or null.</summary>
    /// <remarks>
    /// Trusted because <paramref name="executable"/> is an absolute path this type resolved. A
    /// non-zero exit, empty output, a launch failure, or a hang all mean the same thing to the
    /// caller — not usable yet — so none of them is surfaced as an error here.
    /// </remarks>
    private async Task<string?> TryReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(VersionProbeTimeout);

        try
        {
            var result = await processRunner
                .RunAsync(new ProcessRunRequest(executable, ["--version"]), cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return null;
            }

            var version = result.StandardOutput?.Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The probe's own bound elapsed. Not usable yet, which is a state, not a failure.
            return null;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // A launch failure is the ordinary way an uninitialized alias behaves.
            return null;
        }
    }

    /// <summary>What the package query observed.</summary>
    /// <param name="Registered">Whether the Sandbox package is registered for this user.</param>
    /// <param name="Status">The package's reported status, when observable.</param>
    /// <param name="Detail">Why the query could not answer, when it could not.</param>
    internal readonly record struct SandboxPackagePresence(bool Registered, string? Status, string? Detail = null);

    /// <summary>
    /// Asks the package manager whether the Store-delivered client is registered and healthy.
    /// </summary>
    /// <remarks>
    /// <c>FindPackagesForUser</c> with an empty security ID scopes the query to the current user,
    /// which is what keeps it working unelevated — the all-users overload requires administrative
    /// rights and would throw for every ordinary caller. A query that cannot answer returns
    /// "not observed" rather than "not present", because a probe failure is not evidence of absence.
    /// </remarks>
    private static SandboxPackagePresence DefaultQueryPackage()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return new SandboxPackagePresence(false, null, "package query unavailable on this Windows version");
        }

        return QueryPackageCore();
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static SandboxPackagePresence QueryPackageCore()
    {
        try
        {
            var manager = new PackageManager();
            var package = manager
                .FindPackagesForUser(string.Empty, WindowsSandboxReadiness.PackageFamilyName)
                .FirstOrDefault();

            if (package is null)
            {
                return new SandboxPackagePresence(false, null);
            }

            return new SandboxPackagePresence(true, DescribeStatus(package.Status));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                     or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return new SandboxPackagePresence(false, null, $"package query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders a package status as the short list of conditions that are actually set.
    /// </summary>
    /// <remarks>
    /// <c>PackageStatus</c> exposes named booleans rather than a flags value, so the healthy case
    /// reports "Ok" and anything else names only what is wrong. That is what a user waiting on a
    /// Store download needs to see in a timeout message.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static string DescribeStatus(Windows.ApplicationModel.PackageStatus status)
    {
        if (status.VerifyIsOK())
        {
            return "Ok";
        }

        List<string> conditions = [];

        if (status.NotAvailable)
        {
            conditions.Add(nameof(status.NotAvailable));
        }

        if (status.PackageOffline)
        {
            conditions.Add(nameof(status.PackageOffline));
        }

        if (status.DataOffline)
        {
            conditions.Add(nameof(status.DataOffline));
        }

        if (status.Disabled)
        {
            conditions.Add(nameof(status.Disabled));
        }

        if (status.NeedsRemediation)
        {
            conditions.Add(nameof(status.NeedsRemediation));
        }

        if (status.LicenseIssue)
        {
            conditions.Add(nameof(status.LicenseIssue));
        }

        if (status.Modified)
        {
            conditions.Add(nameof(status.Modified));
        }

        if (status.Tampered)
        {
            conditions.Add(nameof(status.Tampered));
        }

        if (status.DependencyIssue)
        {
            conditions.Add(nameof(status.DependencyIssue));
        }

        if (status.Servicing)
        {
            conditions.Add(nameof(status.Servicing));
        }

        if (status.DeploymentInProgress)
        {
            conditions.Add(nameof(status.DeploymentInProgress));
        }

        return conditions.Count == 0 ? "NotOk" : string.Join('+', conditions);
    }
}

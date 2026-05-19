// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Issue #537: skip the unregister/register pair on `winapp run` / `dotnet run`
// when the AppxManifest on disk is byte-identical to the previously-registered
// manifest. Windows resets per-package capability consent (camera, mic, etc.)
// on every re-registration regardless of RemovalOptions.PreserveApplicationData,
// so this matches Visual Studio's F5 behavior of only re-registering when
// something material has changed.
internal partial class MsixService
{
    // Names that `winapp run` itself writes into the loose-layout output directory
    // when it registers a package. Both `SyncFilesToOutputDirectory` and the
    // recipe copy normalize to one of these; we never register from a raw
    // `Package.appxmanifest` (see PR #542 review L1).
    private static readonly string[] _layoutManifestCandidates = ["AppxManifest.xml", "appxmanifest.xml"];

    /// <summary>
    /// If the conditions for safely skipping re-registration are met, returns an
    /// <see cref="MsixIdentityResult"/> describing the existing registration. Otherwise
    /// returns <c>null</c> and the caller should fall through to the
    /// unregister + register path.
    /// </summary>
    /// <remarks>
    /// Centralizes the skip-path messaging (default-verbosity status + verbose debug detail)
    /// so the two call sites in <c>AddLooseLayoutIdentityAsync</c> stay in lockstep.
    /// </remarks>
    internal MsixIdentityResult? TrySkipRegistration(
        string packageName,
        string publisher,
        string applicationId,
        byte[]? previousManifestBytes,
        FileInfo newManifest,
        DirectoryInfo outputAppXDirectory,
        bool clean,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        if (clean)
        {
            return null;
        }

        if (!IsExistingRegistrationUpToDate(packageName, previousManifestBytes, newManifest, outputAppXDirectory, taskContext, cancellationToken))
        {
            return null;
        }

        // Default verbosity: short, neutral message so users running `winapp run`
        // (or `dotnet run`) can tell the difference between a real re-register
        // and a fast-path skip without needing --verbose.
        taskContext.AddStatusMessage($"{UiSymbols.Check} Reusing existing registration (manifest unchanged)");

        // Verbose: include the path and the rationale.
        taskContext.AddDebugMessage(
            $"{UiSymbols.Check} Package already registered with identical manifest at " +
            $"{outputAppXDirectory.FullName} — skipping re-registration to preserve capability consent");

        return new MsixIdentityResult(packageName, publisher, applicationId);
    }

    /// <summary>
    /// Returns <c>true</c> when the currently installed package for <paramref name="packageName"/>
    /// is a development-mode loose layout rooted at <paramref name="outputAppXDirectory"/> AND its
    /// previously-registered <c>AppxManifest.xml</c> (captured in <paramref name="previousManifestBytes"/>
    /// before the new manifest was written) is byte-identical to <paramref name="newManifest"/>.
    /// </summary>
    /// <param name="previousManifestBytes">
    /// Bytes of the AppxManifest.xml as it existed in <paramref name="outputAppXDirectory"/>
    /// before the current run wrote/copied the new manifest. <c>null</c> means there was no
    /// previous manifest (e.g., first run), in which case the helper always returns <c>false</c>.
    /// We compare against this snapshot (instead of re-reading the installed manifest from the
    /// install location) because, for a loose-layout dev package, the install location IS
    /// <paramref name="outputAppXDirectory"/>, so reading it back would just re-read the file
    /// we already overwrote.
    /// </param>
    /// <remarks>
    /// Conservative by design: returns <c>false</c> on any uncertainty (multiple packages with
    /// the same name, missing install location, read failures, etc.) so the caller falls back
    /// to the safe unregister+register path. <see cref="OperationCanceledException"/> is always
    /// propagated to honor cooperative cancellation.
    /// </remarks>
    internal bool IsExistingRegistrationUpToDate(
        string packageName,
        byte[]? previousManifestBytes,
        FileInfo newManifest,
        DirectoryInfo outputAppXDirectory,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        if (previousManifestBytes is null)
        {
            return false;
        }

        // Cheap, side-effect-free checks first so we don't enumerate installed packages
        // when we already know the manifest can't possibly match.
        if (!newManifest.Exists)
        {
            return false;
        }

        if (previousManifestBytes.Length != newManifest.Length)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var installed = packageRegistrationService.FindDevPackages(packageName);

            // Only short-circuit when exactly one package is registered with this name.
            // Zero means a fresh install is needed; more than one means the existing
            // unregister logic must run to clean up duplicates.
            if (installed.Count != 1)
            {
                return false;
            }

            var pkg = installed[0];

            if (!pkg.IsDevelopmentMode)
            {
                return false;
            }

            if (string.IsNullOrEmpty(pkg.InstallLocation))
            {
                return false;
            }

            string installedFullPath;
            string outputFullPath;
            try
            {
                installedFullPath = Path.GetFullPath(pkg.InstallLocation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                outputFullPath = Path.GetFullPath(outputAppXDirectory.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return false;
            }

            if (!string.Equals(installedFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var newBytes = File.ReadAllBytes(newManifest.FullName);
            return previousManifestBytes.AsSpan().SequenceEqual(newBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any failure inspecting the existing registration is non-fatal — fall back
            // to the unregister+register path so behavior remains correct.
            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not determine if existing registration is up to date: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads the bytes of the loose-layout manifest in <paramref name="outputAppXDirectory"/>
    /// if one is present. Used to snapshot the previous registration's manifest before the
    /// current run overwrites it, so <see cref="IsExistingRegistrationUpToDate"/> can detect
    /// "no change" reliably. Returns <c>null</c> on any I/O failure or if no manifest exists.
    /// </summary>
    /// <remarks>
    /// Only probes the names that <c>winapp run</c> itself produces in the output directory
    /// (<c>AppxManifest.xml</c> / <c>appxmanifest.xml</c>); a stray pre-Sync
    /// <c>Package.appxmanifest</c> is never the artifact that was actually registered,
    /// so we never use it as a comparison baseline. <see cref="OperationCanceledException"/>
    /// is always propagated.
    /// </remarks>
    internal static byte[]? TryReadExistingLayoutManifestBytes(DirectoryInfo outputAppXDirectory)
    {
        if (!outputAppXDirectory.Exists)
        {
            return null;
        }

        foreach (var name in _layoutManifestCandidates)
        {
            var path = Path.Combine(outputAppXDirectory.FullName, name);
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllBytes(path);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through to the next candidate / null
            }
        }

        return null;
    }
}

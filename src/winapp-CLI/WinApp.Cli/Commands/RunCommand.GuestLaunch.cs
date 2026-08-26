// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Handles <see cref="GuestLaunchCommand"/>: verifies the currently registered package for
        /// the given identity is installed from exactly <see cref="GuestLaunchCommand.ExpectedLayoutOption"/>,
        /// then launches it. Never registers or unregisters anything -- a mismatch is refused, not
        /// repaired.
        /// </summary>
        /// <remarks>
        /// This method has no code path that calls <see cref="IPackageRegistrationService.InstallPackageAsync"/>,
        /// <see cref="IMsixService.AddLooseLayoutIdentityAsync"/>, or any unregister API. That is
        /// deliberate: it is what makes this verb safe to run entirely without the target mutation
        /// lock. The ordinary <c>winapp run</c> cannot offer the same guarantee, because its
        /// registration and launch are inseparable steps of one call.
        /// </remarks>
        internal async Task<int> InvokeGuestLaunchAsync(
            System.CommandLine.ParseResult parseResult,
            CancellationToken cancellationToken)
        {
            var packageName = parseResult.GetValue(GuestLaunchCommand.PackageNameOption)!;
            var publisher = parseResult.GetValue(GuestLaunchCommand.PublisherOption)!;
            var applicationId = parseResult.GetValue(GuestLaunchCommand.ApplicationIdOption)!;
            var expectedLayout = parseResult.GetValue(GuestLaunchCommand.ExpectedLayoutOption)!;
            var payload = parseResult.GetValue(GuestLaunchCommand.PayloadOption)!;
            var appArgs = parseResult.GetValue(GuestLaunchCommand.ArgsOption);
            var withAlias = parseResult.GetValue(GuestLaunchCommand.WithAliasOption);
            var debugOutput = parseResult.GetValue(GuestLaunchCommand.DebugOutputOption);
            var unregisterOnExit = parseResult.GetValue(GuestLaunchCommand.UnregisterOnExitOption);
            var detach = parseResult.GetValue(GuestLaunchCommand.DetachOption);
            var useSymbols = parseResult.GetValue(GuestLaunchCommand.SymbolsOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            var familyName = appLauncherService.ComputePackageFamilyName(packageName, publisher);
            var aumid = $"{familyName}!{applicationId}";

            // Exactly one dev-mode package registered under this name, from exactly the layout the
            // caller's own registration phase just used. Anything else -- zero, more than one, a
            // non-dev-mode registration, or a different install location -- is refused outright.
            // There is no fallback path here that registers or unregisters to "fix" a mismatch.
            var candidates = packageRegistrationService.FindDevPackages(packageName)
                .Where(candidate => candidate.IsDevelopmentMode)
                .ToList();

            if (candidates.Count != 1)
            {
                return Fail(
                    candidates.Count == 0
                        ? $"No development-mode package named '{packageName}' is registered. Expected it registered from '{expectedLayout.FullName}'."
                        : $"{candidates.Count} development-mode packages named '{packageName}' are registered; expected exactly one, from '{expectedLayout.FullName}'.",
                    isJson);
            }

            var candidate = candidates[0];

            if (string.IsNullOrEmpty(candidate.InstallLocation) ||
                !TryPathsMatch(candidate.InstallLocation, expectedLayout.FullName))
            {
                return Fail(
                    $"The package named '{packageName}' is registered from '{candidate.InstallLocation}', " +
                    $"not the expected '{expectedLayout.FullName}'. Another deployment may have re-registered " +
                    "it since this run's own registration phase completed.",
                    isJson);
            }

            var packageFullName = appLauncherService.GetPackageFullName(familyName);

            uint processId = 0;
            if (!withAlias)
            {
                processId = appLauncherService.LaunchByAumid(aumid, appArgs);
            }

            return await LaunchRegisteredApplicationAsync(
                aumid, packageName, packageFullName, expectedLayout, payload, appArgs, processId,
                withAlias, debugOutput, unregisterOnExit, detach, useSymbols, isJson, cancellationToken);
        }

        /// <summary>
        /// Compares two install-location paths the same way <c>MsixService.SkipRegistration</c>
        /// does: full path, trailing separators trimmed, ordinal-insensitive.
        /// </summary>
        private static bool TryPathsMatch(string installed, string expected)
        {
            try
            {
                var installedFullPath = Path.GetFullPath(installed)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var expectedFullPath = Path.GetFullPath(expected)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return string.Equals(installedFullPath, expectedFullPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Any failure normalizing either path is treated as a mismatch: this verb only ever
                // refuses on uncertainty, it never falls back to registering or unregistering.
                return false;
            }
        }
    }
}

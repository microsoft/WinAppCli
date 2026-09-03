// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class UnregisterCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Removes exactly one managed package registration from the selected target.
        /// </summary>
        /// <remarks>
        /// Two independent proofs of ownership, both required. The host will only act on a
        /// deployment whose own record says it registered this identity in the current generation,
        /// and the guest is then given the manifest inside that deployment's registration folder, so
        /// its existing install-location check confirms the registration really is rooted there.
        /// A package a user installed in the guest themselves fails both: winapp never recorded it,
        /// and it is not registered from a managed folder.
        /// </remarks>
        private async Task<int> UnregisterOnTargetAsync(
            MsixIdentityResult identity,
            bool isJson,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var target = await orchestrator.PrepareAsync(
                    PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false },
                    cancellationToken);

                var deployment = guestApplicationRunner.FindOwningDeployment(
                    target.Reference,
                    target.Epoch, identity.PackageName, identity.Publisher);

                if (deployment?.Package is not { } package)
                {
                    // Matches the local command: nothing registered is not a failure.
                    if (isJson)
                    {
                        PrintJson([], [], errorMessage: null);
                    }
                    else
                    {
                        logger.LogInformation(
                            "{UISymbol} No package deployed on {Target} for '{PackageName}'.",
                            UiSymbols.Note,
                            target.Reference.Selector,
                            identity.PackageName);
                    }

                    return 0;
                }

                // Verified here rather than left for the guest's own argument parser to discover:
                // guest winapp's `--manifest` validates the path exists before it runs, and a path
                // that does not (for example a registration layout an interrupted `--clean` left
                // without its manifest) fails that parse and prints the guest's usage help instead
                // of a runtime error. Checking first turns that into the same structured,
                // state-repair guidance every other failure here gets.
                var layoutFiles = await target.Operations
                    .ListFilesAsync(GuestPaths.LayoutScope(deployment.DeploymentId), cancellationToken);

                EnsureLayoutHasManifest(layoutFiles, deployment.DeploymentId);

                var exitCode = await target.Operations.ExecuteAsync(
                    new GuestExecRequest
                    {
                        UseGuestWinapp = true,
                        Arguments = GuestRunPlanner.BuildUnregisterArguments(package.RegisteredLocation, isJson),

                        // The guest's install-location check compares against its working directory,
                        // so pointing it at the registration folder is what makes the check prove
                        // this is the managed registration rather than merely a name match.
                        WorkingDirectory = package.RegisteredLocation,
                    },
                    new GuestExecCallbacks(
                        OnStandardOutput: data => WriteRaw(Console.OpenStandardOutput(), data),
                        OnStandardError: data => WriteRaw(Console.OpenStandardError(), data)),
                    cancellationToken);

                if (exitCode.ExitCode == 0)
                {
                    // Cleared only after the guest reported success, so a failed unregister leaves
                    // the record that a later command needs to find the package again.
                    guestApplicationRunner.ClearPackage(target.Reference, deployment);
                }

                return exitCode.ExitCode;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(ansiConsole, isJson, ex.Error);
            }
        }

        private static void WriteRaw(Stream stream, ReadOnlyMemory<byte> data)
        {
            stream.Write(data.Span);
            stream.Flush();
        }

        /// <summary>
        /// Fails with state-repair guidance when a deployment's registration layout is missing its
        /// manifest, instead of letting the guest's own argument parser discover that and print its
        /// usage help.
        /// </summary>
        /// <remarks>
        /// A layout can end up without a manifest when a previous <c>--clean</c> was interrupted
        /// partway through (for example by a locked file). Guest winapp's <c>--manifest</c> option
        /// validates the path exists before the command runs at all, so handing it a missing path
        /// fails argument parsing rather than the command itself — and System.CommandLine's default
        /// response to a parse failure is to print usage help, which looks like a bug in
        /// <c>unregister</c> rather than the actual, repairable cause.
        /// </remarks>
        internal static void EnsureLayoutHasManifest(IReadOnlyList<GuestFileInfo> layoutFiles, string deploymentId)
        {
            ArgumentNullException.ThrowIfNull(layoutFiles);

            if (layoutFiles.Any(file => string.Equals(
                Path.GetFileName(file.RelativePath), "appxmanifest.xml", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.DeploymentDirty,
                "The registration layout for this deployment on the target is missing its manifest, so it cannot be unregistered.",
                userAction: "Redeploy to repair the layout, then unregister again.",
                example: "winapp run . --on sandbox --clean",
                context: new Dictionary<string, string> { ["deploymentId"] = deploymentId });
        }
    }
}

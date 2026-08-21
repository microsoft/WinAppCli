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
        /// Removes exactly one managed guest package registration (spec §"Unregister").
        /// </summary>
        /// <remarks>
        /// Two independent proofs of ownership, both required. The host will only act on a
        /// deployment whose own record says it registered this identity in the current generation,
        /// and the guest is then given the manifest inside that deployment's registration folder, so
        /// its existing install-location check confirms the registration really is rooted there.
        /// A package a user installed in the guest themselves fails both: winapp never recorded it,
        /// and it is not registered from a managed folder.
        /// </remarks>
        private async Task<int> UnregisterInSandboxAsync(
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
                            "{UISymbol} No package deployed in Windows Sandbox for '{PackageName}'.",
                            UiSymbols.Note,
                            identity.PackageName);
                    }

                    return 0;
                }

                var exitCode = await target.Channel.ExecuteAsync(
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
                    guestApplicationRunner.ClearPackage(deployment);
                }

                return exitCode.ExitCode;
            }
            catch (ExecutionTargetException ex)
            {
                return SandboxOutput.Fail(ansiConsole, isJson, ex.Error);
            }
        }

        private static void WriteRaw(Stream stream, ReadOnlyMemory<byte> data)
        {
            stream.Write(data.Span);
            stream.Flush();
        }
    }
}

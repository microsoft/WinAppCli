// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Adds @microsoft/dynwinrt to the user's package.json after codegen
// and prints an install hint.
internal sealed partial class JsBindingsWorkspaceService
{
    public void EnsureRuntimeDependencyAndPrintHint(DirectoryInfo workspaceDirectory)
    {
        const string DynWinrtPackageName = "@microsoft/dynwinrt";

        string version;
        try
        {
            version = npmWrapperVersionProvider.DynWinrtVersion;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "{UISymbol} Could not resolve pinned {Package} version: {Reason}",
                UiSymbols.Note, DynWinrtPackageName, ex.Message);
            return;
        }

        RuntimeDependencyOutcome outcome;
        try
        {
            outcome = userPackageJsonService.EnsureRuntimeDependency(
                workspaceDirectory, DynWinrtPackageName, version);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "{UISymbol} Could not update package.json for {Package}: {Reason}. " +
                "Add it manually to your dependencies.",
                UiSymbols.Note, DynWinrtPackageName, ex.Message);
            return;
        }

        switch (outcome)
        {
            case RuntimeDependencyOutcome.Added:
                var pmAdded = packageManagerDetector.Detect(workspaceDirectory);
                // Info-level so --quiet suppresses; user runs the printed install cmd next.
                logger.LogInformation(
                    "{UISymbol} Added {Package} @ {Version} to your package.json dependencies. Run `{InstallCmd}` to materialize it.",
                    UiSymbols.Check, DynWinrtPackageName, version, pmAdded.InstallCommand);
                break;
            case RuntimeDependencyOutcome.PresentInDevDependencies:
                // Warning: production deploys (npm ci --omit=dev) will break.
                logger.LogWarning(
                    "{UISymbol} {Package} is in devDependencies — generated bindings need it as a production dep. Move it manually.",
                    UiSymbols.Note, DynWinrtPackageName);
                break;
            case RuntimeDependencyOutcome.NoPackageJson:
                logger.LogWarning(
                    "{UISymbol} No package.json found in workspace. Generated bindings will fail to resolve {Package} at runtime. Run `npm init -y` first.",
                    UiSymbols.Warning, DynWinrtPackageName);
                break;
            case RuntimeDependencyOutcome.AlreadyPresent:
            default:
                logger.LogInformation(
                    "{UISymbol} {Package} already declared in package.json dependencies — leaving it alone.",
                    UiSymbols.Check, DynWinrtPackageName);
                break;
        }
    }
}

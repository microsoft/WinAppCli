// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// WorkspaceSetupService — config-init slice.
//
// Owns the logic that decides whether we're "init" or "restore", loads or
// scaffolds winapp.yaml, walks the user through SDK / manifest / dev-mode
// prompts on first run, validates a .NET project's TargetFramework, and
// (for npm callers) prompts which bindings — C++ / JS/TS / Both — to wire
// into winapp.yaml. Result tuple is consumed by SetupWorkspaceAsync to
// decide the rest of the flow.
internal partial class WorkspaceSetupService
{
    private async Task<(int ReturnCode, WinappConfig? Config, bool HadExistingConfig, bool ShouldGenerateManifest, ManifestGenerationInfo? ManifestGenerationInfo, bool ShouldEnableDeveloperMode, string? RecommendedTfm)> InitializeConfigurationAsync(WorkspaceSetupOptions options, bool isDotNetProject, FileInfo? csprojFile, CancellationToken cancellationToken)
    {
        if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null && options.UseDefaults)
        {
            // Default to Stable when --use-defaults
            options.SdkInstallMode = SdkInstallMode.Stable;
        }

        var hadExistingConfig = configService.Exists();
        bool shouldGenerateManifest = true;
        bool shouldEnableDeveloperMode = false;
        string? recommendedTfm = null;
        ManifestGenerationInfo? manifestGenerationInfo = null;
        WinappConfig? config = null;

        // Step 1: Handle configuration requirements
        if (options.RequireExistingConfig && !configService.Exists())
        {
            // Non-.NET project with no winapp.yaml — nothing to restore.
            // (.NET projects without yaml are handled earlier in SetupWorkspaceAsync.)
            // This is a no-op rather than an error: a project that doesn't declare
            // SDK package versions in winapp.yaml has nothing for restore to do.
            logger.LogInformation("{UISymbol} No winapp.yaml found in {ConfigDir}. Nothing to restore.", UiSymbols.Note, options.ConfigDir);
            logger.LogInformation("If this project needs Windows SDK packages, run 'winapp init' to set them up.");
            return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
        }

        // Step 2: Load or prepare configuration
        if (hadExistingConfig)
        {
            config = configService.Load();

            if (config.Packages.Count == 0 && options.RequireExistingConfig && config.JsBindings is null)
            {
                logger.LogInformation("{UISymbol} winapp.yaml found but contains no packages. Nothing to restore.", UiSymbols.Note);
                shouldEnableDeveloperMode = await AskShouldEnableDeveloperModeAsync(options, cancellationToken);
                return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }

            var operation = options.RequireExistingConfig ? "Found" : "Found existing";
            logger.LogDebug("{UISymbol} {Operation} winapp.yaml with {PackageCount} packages", UiSymbols.Package, operation, config.Packages.Count);

            if (!options.RequireExistingConfig && config.Packages.Count > 0)
            {
                logger.LogDebug("{UISymbol} Using pinned package versions from winapp.yaml unless overridden.", UiSymbols.Note);
            }

            // For setup command: ask about overwriting existing config (only if not skipping SDK installation and not config-only mode)
            if (!options.RequireExistingConfig && !options.IgnoreConfig && !options.ConfigOnly && options.SdkInstallMode != SdkInstallMode.None && config.Packages.Count > 0)
            {
                if (options.UseDefaults)
                {
                    options.IgnoreConfig = true;
                }
                else
                {
                    var overwriteConfig = await ShowConfirmationPromptAsync(ansiConsole, "winapp.yaml exists with pinned versions. Overwrite?", cancellationToken);
                    shouldGenerateManifest = await AskShouldGenerateManifestAsync(options, cancellationToken);
                    if (shouldGenerateManifest)
                    {
                        manifestGenerationInfo = await PromptForManifestInfoAsync(options, cancellationToken);
                    }
                    if (!overwriteConfig)
                    {
                        options.IgnoreConfig = true;
                    }
                    else
                    {
                        await AskSdkInstallModeAsync(options, isDotNetProject, csprojFile, cancellationToken);
                    }
                }
            }
        }
        else
        {
            shouldGenerateManifest = await AskShouldGenerateManifestAsync(options, cancellationToken);
            if (shouldGenerateManifest)
            {
                manifestGenerationInfo = await PromptForManifestInfoAsync(options, cancellationToken);
            }

            await AskSdkInstallModeAsync(options, isDotNetProject, csprojFile, cancellationToken);
            if (options.SdkInstallMode != SdkInstallMode.None)
            {
                config = new WinappConfig();
                logger.LogDebug("{UISymbol} No winapp.yaml found; will generate one after setup.", UiSymbols.New);
            }
        }

        // npm-caller bindings prompt: ask whether to wire C++ projections,
        // JS/TS bindings, or both into winapp.yaml. Fills options.AddJsBindings
        // and options.SkipCppProjections so the rest of the flow knows what
        // to generate. No-op when not running via the npm shim, or when an
        // existing yaml already declares jsBindings:.
        var bindingsKind = await AskBindingsKindAsync(options, config, isDotNetProject, cancellationToken);
        options.AddJsBindings = bindingsKind is BindingsKind.JsOnly or BindingsKind.Both;
        options.SkipCppProjections = bindingsKind == BindingsKind.JsOnly;

        // JS/TS bindings target Node/Electron hosts via dynwinrt; .NET projects
        // already have first-class WinRT projections through CsWinRT and the
        // codegen does not produce a .NET-consumable surface. AskBindingsKindAsync
        // already silently downgrades .NET projects to CppOnly, so this guard only
        // catches the case where an existing yaml has a hand-edited `jsBindings:`
        // block on a .NET project.
        if (options.AddJsBindings && isDotNetProject)
        {
            logger.LogError(
                "{UISymbol} JS/TS bindings are not supported on .NET projects — the codegen targets Node/Electron via dynwinrt, and .NET projects already get WinRT via CsWinRT. " +
                "Remove the `jsBindings:` block from winapp.yaml, or re-run from a non-.NET project.",
                UiSymbols.Error);
            return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
        }

        // Re-check after AskSdkInstallModeAsync: the interactive prompt
        // can leave SdkInstallMode=None, which breaks JS bindings.
        if (options.AddJsBindings && options.SdkInstallMode == SdkInstallMode.None)
        {
            logger.LogError(
                "{UISymbol} JS/TS bindings need SDK packages, but the SDK install mode was set to 'none'. " +
                "Re-run and pick a non-'none' SDK mode (stable / preview / experimental), or pick 'C++ only' at the bindings prompt.",
                UiSymbols.Error);
            return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
        }

        // Inject a default jsBindings: block (empty packages: ⇒ all WinAppSDK)
        // when the prompt opted in and the existing yaml hasn't declared one.
        if (options.AddJsBindings && config != null && config.JsBindings is null)
        {
            config.JsBindings = new JsBindingsConfig();
            logger.LogDebug(
                "{UISymbol} Added default jsBindings block (lang={Lang}, output={Output}); empty packages ⇒ full WinAppSDK.",
                UiSymbols.New,
                config.JsBindings.Lang,
                config.JsBindings.Output);

            // Note: @microsoft/dynwinrt is added as a production dep AFTER
            // bindings succeed (JsBindingsWorkspaceService.RunAsync). Doing
            // it here would leave package.json mutated if codegen failed.
        }

        // Persist cppProjections: false when the JS-only choice diverges from
        // the model default (true). Init's later save path round-trips this
        // field through WinappConfigDocument.
        if (options.SkipCppProjections && config != null)
        {
            config.CppProjections = false;
        }

        // .NET: Validate TargetFramework (interactive)
        if (isDotNetProject && csprojFile != null)
        {
            if (dotNetService.IsMultiTargeted(csprojFile))
            {
                logger.LogError("The project '{CsprojFile}' uses multi-targeting (TargetFrameworks). winapp init does not support multi-targeted projects.", csprojFile.Name);
                return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }

            var currentTfm = dotNetService.GetTargetFramework(csprojFile);
            logger.LogDebug("Current TargetFramework: {Tfm}", currentTfm ?? "(not set)");

            if (currentTfm == null || !dotNetService.IsTargetFrameworkSupported(currentTfm))
            {
                recommendedTfm = dotNetService.GetRecommendedTargetFramework(currentTfm);

                if (!options.UseDefaults)
                {
                    var currentDisplay = currentTfm ?? "(not set)";

                    var promptSuffix = options.SdkInstallMode != SdkInstallMode.None
                        ? " (Required for Windows App SDK)"
                        : "";

                    var shouldUpdate = await ShowConfirmationPromptAsync(ansiConsole, $"Update TargetFramework to \"{recommendedTfm}\"{promptSuffix}?", cancellationToken);

                    if (!shouldUpdate)
                    {
                        if (options.SdkInstallMode != SdkInstallMode.None)
                        {
                            logger.LogError("TargetFramework '{Tfm}' is not supported for Windows App SDK. Cannot continue.", currentDisplay);
                            return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
                        }

                        // Not installing SDKs, so TFM update is not required — skip it
                        recommendedTfm = null;
                    }
                }
                else
                {
                    var currentDisplay = currentTfm ?? "(not set)";
                    logger.LogWarning(
                        "TargetFramework '{CurrentTfm}' is not supported for Windows App SDK. Automatically updating to '{RecommendedTfm}' because --use-defaults was specified.",
                        currentDisplay,
                        recommendedTfm);
                    logger.LogInformation("Automatically updating TargetFramework from {CurrentTfm} to {RecommendedTfm} because --use-defaults was specified.", Markup.Escape(currentDisplay), recommendedTfm);
                }
            }
            else
            {
                logger.LogDebug("{UISymbol} TargetFramework '{Tfm}' is supported", UiSymbols.Check, currentTfm);
            }
        }

        shouldEnableDeveloperMode = await AskShouldEnableDeveloperModeAsync(options, cancellationToken);

        return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
    }
}

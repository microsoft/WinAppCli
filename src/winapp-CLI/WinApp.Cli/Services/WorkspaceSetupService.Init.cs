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
// emits the default jsBindings: block when `--js-bindings` is supplied.
// Result tuple is consumed by SetupWorkspaceAsync to decide the rest of
// the flow.
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

            if (config.Packages.Count == 0 && options.RequireExistingConfig)
            {
                logger.LogInformation("{UISymbol} winapp.yaml found but contains no packages. Nothing to restore.", UiSymbols.Note);
                shouldEnableDeveloperMode = await AskShouldEnableDeveloperModeAsync(options, cancellationToken);
                return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }

            var operation = options.RequireExistingConfig ? "Found" : "Found existing";
            logger.LogDebug("{UISymbol} {Operation} winapp.yaml with {PackageCount} packages", UiSymbols.Package, operation, config.Packages.Count);

            // Re-init hint: surface JS bindings capability for npm-shim users
            // who haven't opted in (winget users can't use --js-bindings).
            if (!options.RequireExistingConfig
                && !options.AddJsBindings
                && config.JsBindings is null
                && string.Equals(
                    Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER"),
                    "nodejs-package",
                    StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "{UISymbol} To add JS/TS bindings to this project, re-run: npx winapp init --js-bindings",
                    UiSymbols.Info);
            }

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

        // Re-check after AskSdkInstallModeAsync: the interactive prompt
        // can leave SdkInstallMode=None, which breaks --js-bindings.
        if (options.AddJsBindings && options.SdkInstallMode == SdkInstallMode.None)
        {
            logger.LogError(
                "{UISymbol} --js-bindings requires SDK packages but the SDK install mode was set to 'none'. " +
                "Re-run without --js-bindings, or pick a non-'none' SDK mode (stable / preview / experimental).",
                UiSymbols.Error);
            return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
        }

        // --js-bindings: fill a default block when none exists; never overwrite.
        if (options.AddJsBindings && config != null && config.JsBindings is not null)
        {
            // Warn when override flags are ignored because a block already exists.
            var hasOverrides = !string.IsNullOrWhiteSpace(options.JsBindingsOutputOverride)
                || !string.IsNullOrWhiteSpace(options.JsBindingsLangOverride)
                || (options.JsBindingsPresets is { Count: > 0 });
            if (hasOverrides)
            {
                logger.LogWarning(
                    "{UISymbol} --js-bindings-output / --js-bindings-lang / --js-bindings-{{preset}} are " +
                    "ignored because winapp.yaml already declares a jsBindings block. " +
                    "Use 'npx winapp node jsbindings add --force' to overwrite specific fields.",
                    UiSymbols.Warning);
            }
        }
        if (options.AddJsBindings && config != null && config.JsBindings is null)
        {
            var jsCfg = new JsBindingsConfig();
            if (!string.IsNullOrWhiteSpace(options.JsBindingsOutputOverride))
            {
                jsCfg.Output = options.JsBindingsOutputOverride!.Trim();
            }
            if (!string.IsNullOrWhiteSpace(options.JsBindingsLangOverride))
            {
                jsCfg.Lang = options.JsBindingsLangOverride!.Trim();
            }

            // Validate the resolved output path before persisting.
            try
            {
                DynWinrtCodegenService.ResolveOutputDir(options.BaseDirectory, jsCfg.Output);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(
                    "{UISymbol} Invalid --js-bindings-output: {Reason}",
                    UiSymbols.Error, ex.Message);
                return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }
            if (options.JsBindingsPresets is { Count: > 0 } presetNames)
            {
                var packageIds = JsBindingsPresets.ResolveAndUnion(presetNames);
                if (packageIds.Count > 0)
                {
                    jsCfg.Packages = new List<string>(packageIds);
                    logger.LogDebug(
                        "{UISymbol} jsBindings presets [{Presets}] → packages=[{Packages}]",
                        UiSymbols.New,
                        string.Join(", ", presetNames),
                        string.Join(", ", packageIds));
                }
                else
                {
                    // Defensive: InitCommand validates preset names upstream.
                    logger.LogWarning(
                        "{UISymbol} jsBindings presets [{Presets}] resolved to no prefixes; ignoring (known: {Known}).",
                        UiSymbols.Warning,
                        string.Join(", ", presetNames),
                        JsBindingsPresets.KnownPresetsDisplay());
                }
            }
            config.JsBindings = jsCfg;
            logger.LogDebug(
                "{UISymbol} --js-bindings: added default jsBindings block (lang={Lang}, output={Output})",
                UiSymbols.New,
                config.JsBindings.Lang,
                config.JsBindings.Output);

            // Note: @microsoft/dynwinrt is added as a production dep AFTER
            // bindings succeed (JsBindingsWorkspaceService.RunAsync). Doing
            // it here would leave package.json mutated if codegen failed.
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

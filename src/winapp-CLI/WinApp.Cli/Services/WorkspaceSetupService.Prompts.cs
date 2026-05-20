// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// WorkspaceSetupService — Spectre.Console prompt slice.
//
// Holds every interactive prompt the init/restore flow needs (manifest
// generation, developer-mode toggle, SDK install mode, .csproj picker,
// confirmation). Splitting these out keeps the orchestration file focused
// on control flow and lets us evolve UI without churning the main file.
internal partial class WorkspaceSetupService
{
    // Selects the .csproj file to configure when multiple are found.
    private async Task<FileInfo> SelectCsprojFileAsync(IReadOnlyList<FileInfo> csprojFiles, CancellationToken cancellationToken)
    {
        if (csprojFiles.Count == 1)
        {
            return csprojFiles[0];
        }

        // Multiple .csproj files found — ask the user which one to use
        var choices = csprojFiles.Select(f => f.Name).ToArray();
        var selected = await ansiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Multiple .csproj files found. Which project should be configured?")
                .AddChoices(choices),
            cancellationToken);
        return csprojFiles.First(f => f.Name == selected);
    }

    private static async Task<bool> ShowConfirmationPromptAsync(IAnsiConsole ansiConsole, string prompt, CancellationToken cancellationToken)
    {
        var result = await ansiConsole.PromptAsync(new ConfirmationPrompt(prompt), cancellationToken);

        ansiConsole.Cursor.MoveUp();
        ansiConsole.Write("\x1b[2K"); // Clear line
        ansiConsole.MarkupLine($"{prompt}: [underline]{(result ? "Yes" : "No")}[/]");

        return result;
    }

    private async Task<ManifestGenerationInfo?> PromptForManifestInfoAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigOnly)
        {
            return null;
        }

        return await manifestService.PromptForManifestInfoAsync(options.BaseDirectory, null, null, "1.0.0.0", "Windows Application", null, options.UseDefaults, cancellationToken);
    }

    private async Task<bool> AskShouldEnableDeveloperModeAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigOnly || options.RequireExistingConfig)
        {
            return false;
        }

        if (devModeService.IsEnabled())
        {
            return false;
        }

        if (options.UseDefaults)
        {
            return false;
        }

        return await ShowConfirmationPromptAsync(ansiConsole, "Enable Developer Mode (requires elevation and you will be prompted by User Account Control)", cancellationToken);
    }

    private async Task<bool> AskShouldGenerateManifestAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.RequireExistingConfig)
        {
            return true;
        }

        // Check if manifest already exists, and if so, ask about overwriting
        var manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider, options.BaseDirectory);
        if ((manifestPath?.Exists) == true)
        {
            logger.LogDebug("{UISymbol} {ManifestFileName} already exists at {ManifestPath}", UiSymbols.Check, manifestPath.Name, manifestPath.FullName);
            if (options.UseDefaults)
            {
                // With --use-defaults, skip overwriting existing manifest (non-destructive)
                return false;
            }
            else
            {
                return await ShowConfirmationPromptAsync(ansiConsole, $"{manifestPath.Name} already exists. Overwrite?", cancellationToken);
            }
        }

        return true;
    }

    private async Task AskSdkInstallModeAsync(WorkspaceSetupOptions options, bool isDotNetProject, FileInfo? csprojFile, CancellationToken cancellationToken)
    {
        // For init (not restore), prompt for SDK installation choice if not specified
        if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null)
        {
            // If the .NET project already references WinAppSDK, skip the prompt and default to None.
            // This call may take a while on a fresh machine because `dotnet list package` triggers
            // an implicit restore — surface a spinner so the user knows we're doing something (#463).
            if (isDotNetProject && csprojFile != null)
            {
                var alreadyReferencesWinAppSdk = await RunWithStatusAsync(
                    "Detecting project SDK references...",
                    ct => dotNetService.HasPackageReferenceAsync(csprojFile, DotNetService.WINAPP_SDK_NUGET_PACKAGE, ct),
                    cancellationToken);
                if (alreadyReferencesWinAppSdk)
                {
                    options.SdkInstallMode = SdkInstallMode.None;
                    logger.LogInformation("{UISymbol} Project already references {PackageName}; skipping Windows App SDK setup.", UiSymbols.Check, DotNetService.WINAPP_SDK_NUGET_PACKAGE);
                    return;
                }
            }
            // Determine which packages to show versions for
            var packages = isDotNetProject
                ? [BuildToolsService.WINAPP_SDK_PACKAGE]
                : new[] { BuildToolsService.CPP_SDK_PACKAGE, BuildToolsService.WINAPP_SDK_PACKAGE };

            // Fetch versions for all modes in parallel (failures are non-fatal). On a fresh machine
            // these NuGet feed calls can take many seconds; show a spinner so the prompt doesn't
            // appear to hang (#463).
            var modes = new[] { SdkInstallMode.Stable, SdkInstallMode.Preview, SdkInstallMode.Experimental };
            var versionTasks = await RunWithStatusAsync(
                "Fetching latest SDK versions...",
                async ct =>
                {
                    var tasks = modes
                        .SelectMany(mode => packages.Select(pkg => (Mode: mode, Package: pkg, Task: SafeGetLatestVersionAsync(pkg, mode, ct))))
                        .ToList();
                    await Task.WhenAll(tasks.Select(v => v.Task));
                    return tasks;
                },
                cancellationToken);

            // Build a lookup: (mode) → version label
            var versionsByMode = modes.ToDictionary(
                mode => mode,
                mode =>
                {
                    var parts = versionTasks
                        .Where(v => v.Mode == mode && v.Task.Result != null)
                        .Select(v => $"{(v.Package == BuildToolsService.CPP_SDK_PACKAGE ? "Windows SDK" : "Windows App SDK")} [green]{v.Task.Result}[/]");
                    return string.Join(", ", parts);
                });

            var label = isDotNetProject ? "Windows App SDK" : "SDKs";
            string FormatChoice(string modeLabel, SdkInstallMode mode)
            {
                var versions = versionsByMode[mode];
                return string.IsNullOrEmpty(versions)
                    ? $"Setup {modeLabel} {label}"
                    : $"Setup {modeLabel} {label} ({versions})";
            }
            string[] sdkChoices = [
                FormatChoice("Stable", SdkInstallMode.Stable),
                FormatChoice("Preview", SdkInstallMode.Preview),
                FormatChoice("Experimental", SdkInstallMode.Experimental),
                $"Do not setup {label}"
            ];

            ansiConsole.WriteLine($"Select {label} setup option:");
            var sdkPrompt = new SelectionPrompt<string>()
                .AddChoices(sdkChoices);

            var sdkChoice = await ansiConsole.PromptAsync(sdkPrompt, cancellationToken);

            ansiConsole.Cursor.MoveUp();
            ansiConsole.Write("\x1b[2K"); // Clear line

            if (sdkChoice == sdkChoices[0])
            {
                options.SdkInstallMode = SdkInstallMode.Stable;
            }
            else if (sdkChoice == sdkChoices[1])
            {
                options.SdkInstallMode = SdkInstallMode.Preview;
            }
            else if (sdkChoice == sdkChoices[2])
            {
                options.SdkInstallMode = SdkInstallMode.Experimental;
            }
            else
            {
                options.SdkInstallMode = SdkInstallMode.None;
                logger.LogInformation("Setup {Label}: Do not setup {Label}", label, label);
                return;
            }

            ansiConsole.MarkupLine($"Setup {label}: [underline]{Markup.Remove(sdkChoice["Setup ".Length..])}[/]");
        }
    }

    private async Task<string?> SafeGetLatestVersionAsync(string packageName, SdkInstallMode mode, CancellationToken cancellationToken)
    {
        try
        {
            return await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode: mode, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to fetch latest version for {PackageName} ({Mode}): {ErrorMessage}", packageName, mode, ex.Message);
            return null;
        }
    }

    // Runs work while showing a Spectre.Console spinner with message.
    // In non-interactive contexts (redirected output, no Information logging),
    // falls back to a single log line so the user still sees what's happening (#463).
    private async Task<T> RunWithStatusAsync<T>(string message, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (Environment.UserInteractive
            && !Console.IsOutputRedirected
            && logger.IsEnabled(LogLevel.Information)
            && ansiConsole.Profile.Capabilities.Interactive)
        {
            T result = default!;
            await ansiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(message, async _ =>
                {
                    result = await work(cancellationToken);
                });
            return result;
        }

        logger.LogInformation("{Message}", message);
        return await work(cancellationToken);
    }

    // Result of the npm-caller bindings prompt.
    private enum BindingsKind
    {
        CppOnly,
        JsOnly,
        Both,
    }

    // Asks the user (npm caller only) which bindings to generate for this
    // workspace: C++ projections, JS/TS bindings, or both. Defaults to Both
    // under --use-defaults. Returns CppOnly (the historical default) for
    // non-npm callers so winget / standalone-CLI users see no behavior change.
    private async Task<BindingsKind> AskBindingsKindAsync(WorkspaceSetupOptions options, WinappConfig? existingConfig, CancellationToken cancellationToken)
    {
        // Restore (winapp restore) never re-prompts: it respects whatever the
        // existing yaml already declares.
        if (options.RequireExistingConfig)
        {
            return BindingsKindFromConfig(existingConfig);
        }

        // Standalone CLI (winget / native binary) keeps its current C++ default.
        var caller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        if (!string.Equals(caller, "nodejs-package", StringComparison.Ordinal))
        {
            return BindingsKind.CppOnly;
        }

        // Existing yaml that already declares jsBindings: — don't change the
        // user's earlier choice. Map it back to a kind so callers can still
        // gate on AddJsBindings / SkipCppProjections.
        if (existingConfig?.JsBindings is not null)
        {
            return BindingsKindFromConfig(existingConfig);
        }

        // Non-interactive: default to Both so `npx winapp init --use-defaults`
        // (sample tests, CI) wires up everything the npm wrapper enables.
        if (options.UseDefaults)
        {
            return BindingsKind.Both;
        }

        var choices = new[]
        {
            "Both C++ and JS/TS bindings (default)",
            "JS/TS bindings only",
            "C++ projections only",
        };

        ansiConsole.WriteLine("Select which bindings to generate:");
        var pick = await ansiConsole.PromptAsync(
            new SelectionPrompt<string>().AddChoices(choices),
            cancellationToken);

        ansiConsole.Cursor.MoveUp();
        ansiConsole.Write("\x1b[2K"); // Clear line
        ansiConsole.MarkupLine($"Bindings: [underline]{Markup.Remove(pick)}[/]");

        return pick switch
        {
            "JS/TS bindings only" => BindingsKind.JsOnly,
            "C++ projections only" => BindingsKind.CppOnly,
            _ => BindingsKind.Both,
        };
    }

    private static BindingsKind BindingsKindFromConfig(WinappConfig? config)
    {
        if (config?.JsBindings is null)
        {
            return BindingsKind.CppOnly;
        }
        return config.CppProjections ? BindingsKind.Both : BindingsKind.JsOnly;
    }
}

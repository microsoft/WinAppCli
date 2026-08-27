// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UnregisterCommand : Command, IShortDescription
{
    public string ShortDescription => "Unregister a sideloaded development package.";

    public static Argument<FileInfo> InputArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<bool> ForceOption { get; }
    public static Option<bool> PruneOption { get; }

    static UnregisterCommand()
    {
        InputArgument = new Argument<FileInfo>("input")
        {
            Description = "Path to a .NET file-based app (a single .cs) whose package should be unregistered. Its identity is resolved the same way 'winapp run' resolves it, so no manifest path is needed. Omit to use --manifest or auto-detect a manifest in the current directory. Cannot be combined with --manifest.",
            Arity = ArgumentArity.ZeroOrOne
        };
        InputArgument.AcceptExistingOnly();

        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the Package.appxmanifest (default: auto-detect from current directory)"
        };
        ManifestOption.AcceptExistingOnly();

        ForceOption = new Option<bool>("--force")
        {
            Description = "Skip the install-location directory check and unregister even if the package was registered from a different project tree. With --prune, also skips the confirmation prompt."
        };

        PruneOption = new Option<bool>("--prune")
        {
            Description = "Remove every development-mode registration whose files are gone. These can never launch — Windows keeps the identity and its Start menu entry, but activation silently does nothing. Lists what it found and asks before removing; pass --force to skip the prompt. Cannot be combined with an input or --manifest."
        };
    }

    public UnregisterCommand() : base("unregister", "Unregisters a sideloaded development package. Only removes packages registered in development mode (e.g., via 'winapp run' or 'create-debug-identity').")
    {
        Arguments.Add(InputArgument);
        Options.Add(ManifestOption);
        Options.Add(ForceOption);
        Options.Add(PruneOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IPackageRegistrationService packageRegistrationService,
        IProjectRunService projectRunService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        ILogger<UnregisterCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var input = parseResult.GetValue(InputArgument);
            var manifest = parseResult.GetValue(ManifestOption);
            var force = parseResult.GetValue(ForceOption);
            var prune = parseResult.GetValue(PruneOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (prune)
            {
                if (input != null || manifest != null)
                {
                    return FailWith(
                        "--prune sweeps every dev registration whose files are gone, so it cannot be combined with an input or --manifest. Run it on its own.",
                        isJson);
                }

                return await PruneOrphanedRegistrationsAsync(force, isJson, cancellationToken);
            }

            // An input and --manifest are two different ways to name a package, and they can name
            // DIFFERENT ones. Silently preferring either would let the command remove a registration —
            // and its app data — that the user did not ask for, so the ambiguity is rejected instead.
            if (input != null && manifest != null)
            {
                return FailWith(
                    $"'{input.Name}' and --manifest name the package two different ways, and they can resolve to different packages. Pass one or the other.",
                    isJson);
            }

            string packageName;
            string trustedRoot;

            if (input != null)
            {
                if (!ProjectRunService.IsSingleFileApp(input))
                {
                    return FailWith(
                        $"'{input.Name}' is not a .NET file-based app. Pass a single .cs file, or use --manifest to name a package's manifest.",
                        isJson);
                }

                SingleFileIdentityResolution resolved;
                try
                {
                    resolved = await projectRunService.ResolveSingleFileIdentityAsync(input, cancellationToken);
                }
                catch (ProjectRunException ex)
                {
                    return FailWith(ex.Message, isJson);
                }

                if (resolved.Packaging == ProjectPackaging.Unpackaged)
                {
                    // Nothing was ever registered, so reporting "no package found" would read as a failure
                    // to find something that should exist.
                    if (!isJson)
                    {
                        logger.LogInformation(
                            "{UISymbol} '{File}' is an unpackaged app (WindowsPackageType=None), so it has no registration to remove.",
                            UiSymbols.Note, input.Name);
                    }
                    else
                    {
                        PrintJson([], [], errorMessage: null);
                    }

                    return 0;
                }

                packageName = resolved.PackageName;

                // A file-based app's layout lives in the SDK's own %TEMP%\dotnet\runfile\<stem>-<hash>
                // directory, never under the user's working directory, so the guard is scoped to that
                // per-file build root instead. That is strictly more precise than a directory-tree
                // heuristic: it confirms the registration came from THIS .cs rather than a same-named
                // app elsewhere — the collision `winapp run` warns about.
                trustedRoot = resolved.BuildRootDirectory ?? string.Empty;
            }
            else
            {
                // Resolve manifest
                FileInfo resolvedManifest;
                if (manifest != null && manifest.Exists)
                {
                    resolvedManifest = manifest;
                }
                else
                {
                    resolvedManifest = ManifestHelper.FindManifest(currentDirectoryProvider.GetCurrentDirectory());
                    if (!resolvedManifest.Exists)
                    {
                        return FailWith(
                            "No manifest found in the current directory. Pass a .cs file-based app, or use --manifest to specify the path.",
                            isJson);
                    }
                }

                // Parse package name from manifest
                var manifestContent = await File.ReadAllTextAsync(resolvedManifest.FullName, Encoding.UTF8, cancellationToken);
                var identity = MsixService.ParseAppxManifestAsync(manifestContent);
                packageName = identity.PackageName;

                // Scope the "different project tree" guard to the resolved manifest's directory, not the
                // current directory. When the manifest is auto-detected these are the same folder, so this
                // is unchanged for the common case; they diverge for an explicit --manifest, where the tree
                // the caller means is the one their manifest describes.
                trustedRoot = Path.GetFullPath(
                    resolvedManifest.DirectoryName ?? currentDirectoryProvider.GetCurrentDirectory());
            }

            // Search for both the exact name and the .debug variant
            var namesToCheck = new[] { packageName, $"{packageName}.debug" };

            var unregistered = new List<string>();
            var skipped = new List<string>();

            foreach (var name in namesToCheck)
            {
                var packages = packageRegistrationService.FindDevPackages(name);

                foreach (var pkg in packages)
                {
                    if (!pkg.IsDevelopmentMode)
                    {
                        if (!isJson)
                        {
                            logger.LogInformation("{UISymbol} {FullName}: installed via MSIX/Store, skipping.", UiSymbols.Note, pkg.FullName);
                        }
                        skipped.Add(pkg.FullName);
                        continue;
                    }

                    // Check the install location sits under the tree the caller identified
                    if (!force && !string.IsNullOrEmpty(pkg.InstallLocation) && trustedRoot.Length > 0)
                    {
                        var installPath = Path.GetFullPath(pkg.InstallLocation);
                        if (!installPath.StartsWith(trustedRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!isJson)
                            {
                                logger.LogWarning("{UISymbol} {FullName}: registered from a different project tree ({Location}). Use --force to override.",
                                    UiSymbols.Warning, pkg.FullName, pkg.InstallLocation);
                            }
                            skipped.Add(pkg.FullName);
                            continue;
                        }
                    }

                    // Explicit unregister command — remove package and its data
                    await packageRegistrationService.UnregisterAsync(name, preserveAppData: false, cancellationToken);

                    if (!isJson)
                    {
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Unregistered {pkg.FullName}");
                    }
                    unregistered.Add(pkg.FullName);
                }
            }

            if (isJson)
            {
                PrintJson(unregistered, skipped, errorMessage: null);
            }
            else if (unregistered.Count == 0 && skipped.Count == 0)
            {
                logger.LogInformation("{UISymbol} No dev-registered package found for '{PackageName}'.", UiSymbols.Note, packageName);
            }

            return 0;

            int FailWith(string message, bool json)
            {
                if (json)
                {
                    PrintJson([], [], message);
                }
                else
                {
                    logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
                }

                return 1;
            }
        }

        /// <summary>
        /// Removes every development-mode registration whose files are gone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These registrations can never launch — Windows keeps the identity and its Start-menu entry,
        /// but activation silently does nothing, so the entry looks broken with no way to tell what it
        /// is. They accumulate whenever a build output or project tree is deleted while the package
        /// stays registered; a file-based app is especially exposed, because its layout lives under
        /// <c>%LOCALAPPDATA%\Temp</c>, which Windows cleans on its own schedule.
        /// </para>
        /// <para>
        /// Deliberately confirms first. "The install location does not resolve" is nearly always a
        /// deleted folder, but it also describes a package registered from a disconnected network share
        /// or removable drive, which would come back when the device does. Listing the candidates lets
        /// the user notice that before anything is removed; <c>--force</c> skips the prompt, and a
        /// non-interactive run requires it rather than silently assuming consent.
        /// </para>
        /// </remarks>
        private async Task<int> PruneOrphanedRegistrationsAsync(bool force, bool isJson, CancellationToken cancellationToken)
        {
            var orphans = packageRegistrationService.FindOrphanedDevPackages();

            if (orphans.Count == 0)
            {
                if (isJson)
                {
                    PrintJson([], [], errorMessage: null);
                }
                else
                {
                    logger.LogInformation("{UISymbol} No dev registrations with missing files.", UiSymbols.Note);
                }

                return 0;
            }

            if (!isJson)
            {
                ansiConsole.MarkupLineInterpolated(
                    $"{UiSymbols.Package} {orphans.Count} dev registration(s) whose files are gone:");
                foreach (var orphan in orphans)
                {
                    ansiConsole.MarkupLineInterpolated($"  [dim]{orphan.FullName}[/]");
                }
            }

            if (!force)
            {
                if (isJson || !ansiConsole.Profile.Capabilities.Interactive)
                {
                    return FailWith(
                        $"--prune found {orphans.Count} dev registration(s) whose files are gone, but cannot prompt for confirmation here. Re-run with --force to remove them.",
                        isJson);
                }

                var confirmed = await ansiConsole.PromptAsync(
                    new ConfirmationPrompt($"Unregister {orphans.Count} package(s)?"), cancellationToken);
                if (!confirmed)
                {
                    logger.LogInformation("{UISymbol} Nothing removed.", UiSymbols.Note);
                    return 0;
                }
            }

            var unregistered = new List<string>();
            var skipped = new List<string>();

            foreach (var orphan in orphans)
            {
                try
                {
                    // By full name, not identity name: prune targets exactly the registrations it listed,
                    // so a same-named package that IS still installed from a live location is untouched.
                    // The files are already gone, so there is no app data worth preserving.
                    await packageRegistrationService.UnregisterByFullNameAsync(orphan.FullName, preserveAppData: false, cancellationToken);
                    unregistered.Add(orphan.FullName);

                    if (!isJson)
                    {
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Unregistered {orphan.FullName}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One package that refuses to go must not abandon the rest of the sweep.
                    skipped.Add(orphan.FullName);
                    logger.LogWarning("{UISymbol} {FullName}: {Message}", UiSymbols.Warning, orphan.FullName, ex.Message);
                }
            }

            if (isJson)
            {
                PrintJson(unregistered, skipped, errorMessage: null);
            }

            return 0;

            int FailWith(string message, bool json)
            {
                if (json)
                {
                    PrintJson([], [], message);
                }
                else
                {
                    logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
                }

                return 1;
            }
        }

        private void PrintJson(List<string> unregistered, List<string> skipped, string? errorMessage)
        {
            var result = new UnregisterResult
            {
                Unregistered = unregistered.Count > 0 ? unregistered : null,
                Skipped = skipped.Count > 0 ? skipped : null,
                Error = errorMessage
            };

            var json = JsonSerializer.Serialize(result, UnregisterJsonContext.Default.UnregisterResult);
            ansiConsole.WriteLine(json);
        }
    }
}

internal sealed class UnregisterResult
{
    public List<string>? Unregistered { get; set; }
    public List<string>? Skipped { get; set; }
    public string? Error { get; set; }
}

[JsonSerializable(typeof(UnregisterResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UnregisterJsonContext : JsonSerializerContext;

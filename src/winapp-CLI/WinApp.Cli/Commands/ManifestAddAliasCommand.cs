// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class ManifestAddAliasCommand : Command, IShortDescription
{
    public string ShortDescription => "Add an execution alias to the app manifest";

    public static Option<string> NameOption { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<string> AppIdOption { get; }
    public static Option<bool> UpdateCsprojOption { get; }

    static ManifestAddAliasCommand()
    {
        NameOption = new Option<string>("--name")
        {
            Description = "Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest."
        };

        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory)"
        };
        ManifestOption.AcceptExistingOnly();

        AppIdOption = new Option<string>("--app-id")
        {
            Description = "Application Id to add the alias to (default: first Application element)"
        };

        UpdateCsprojOption = new Option<bool>("--update-csproj")
        {
            Description = "Also set <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias> in the .NET project (.csproj) next to the manifest, " +
                "so 'winapp run' / 'dotnet run' launch the app through this execution alias and keep console I/O in the current terminal."
        };
    }

    public ManifestAddAliasCommand() : base("add-alias", "Add an execution alias (uap5:AppExecutionAlias) to a Package.appxmanifest. " +
        "This allows launching the packaged app from the command line by typing the alias name. " +
        "By default, the alias is inferred from the Executable attribute (e.g. $targetnametoken$.exe becomes $targetnametoken$.exe alias).")
    {
        Options.Add(NameOption);
        Options.Add(ManifestOption);
        Options.Add(AppIdOption);
        Options.Add(UpdateCsprojOption);
    }

    public class Handler(IManifestService manifestService, IDotNetService dotNetService, ICurrentDirectoryProvider currentDirectoryProvider, ILogger<ManifestAddAliasCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var aliasName = parseResult.GetValue(NameOption);
            var manifestFile = parseResult.GetValue(ManifestOption);
            var appId = parseResult.GetValue(AppIdOption);
            var updateCsproj = parseResult.GetValue(UpdateCsprojOption);

            // Find manifest
            FileInfo? resolvedManifest = manifestFile;
            if (resolvedManifest == null)
            {
                resolvedManifest = MsixService.FindProjectManifest(currentDirectoryProvider);
                if (resolvedManifest == null || !resolvedManifest.Exists)
                {
                    logger.LogError("{UISymbol} Could not find Package.appxmanifest in the current directory. Use --manifest to specify the path.", UiSymbols.Error);
                    return 1;
                }
            }

            var options = new AddExecutionAliasOptions(resolvedManifest, aliasName, appId);
            var result = await manifestService.AddExecutionAliasAsync(options, cancellationToken);

            switch (result.Status)
            {
                case AddExecutionAliasStatus.Added:
                    logger.LogInformation("{UISymbol} Added execution alias '{Alias}' to {Manifest}", UiSymbols.Check, result.AliasName, resolvedManifest.FullName);
                    if (updateCsproj)
                    {
                        await UpdateCsprojAsync(resolvedManifest, cancellationToken);
                    }
                    return 0;

                case AddExecutionAliasStatus.AlreadyExists:
                    logger.LogInformation("{UISymbol} Execution alias '{Alias}' already exists in the manifest.", UiSymbols.Warning, result.AliasName);
                    if (updateCsproj)
                    {
                        await UpdateCsprojAsync(resolvedManifest, cancellationToken);
                    }
                    return 0;

                case AddExecutionAliasStatus.ConflictingAliasExists:
                    logger.LogError("{UISymbol} Application already has an execution alias '{ExistingAlias}'. Only one execution alias per application is supported. Remove the existing alias first or use the same name.", UiSymbols.Error, result.ExistingAlias);
                    return 1;

                case AddExecutionAliasStatus.NoApplicationElement:
                    logger.LogError("{UISymbol} No <Application> element found in the manifest.", UiSymbols.Error);
                    return 1;

                case AddExecutionAliasStatus.ApplicationIdNotFound:
                    logger.LogError("{UISymbol} No <Application> element with Id='{AppId}' found in the manifest.", UiSymbols.Error, appId);
                    return 1;

                case AddExecutionAliasStatus.CouldNotInferAlias:
                    logger.LogError("{UISymbol} Could not infer alias name from Executable attribute. Use --name to specify the alias.", UiSymbols.Error);
                    return 1;

                case AddExecutionAliasStatus.InvalidAliasName:
                    logger.LogError(
                        "{UISymbol} Alias name '{Alias}' is not a valid bare .exe filename. Aliases must be a single .exe filename with no path separators, drive letters, '..' segments, trailing dots/spaces, or reserved device names (CON, NUL, COM1-9, LPT1-9).",
                        UiSymbols.Error,
                        result.AliasName);
                    return 1;

                case AddExecutionAliasStatus.ManifestParseError:
                    logger.LogError("{UISymbol} Failed to parse manifest: {Error}", UiSymbols.Error, result.ErrorMessage);
                    return 1;

                case AddExecutionAliasStatus.ManifestEmpty:
                    logger.LogError("{UISymbol} Manifest has no root element.", UiSymbols.Error);
                    return 1;

                default:
                    logger.LogError("{UISymbol} Unexpected error adding execution alias.", UiSymbols.Error);
                    return 1;
            }
        }

        private async Task UpdateCsprojAsync(FileInfo manifest, CancellationToken cancellationToken)
        {
            var projectDirectory = manifest.Directory;
            if (projectDirectory == null || !projectDirectory.Exists)
            {
                logger.LogWarning("{UISymbol} Could not locate the project directory for the manifest; skipped updating the .csproj.", UiSymbols.Warning);
                return;
            }

            var csprojFiles = dotNetService.FindCsproj(projectDirectory);
            if (csprojFiles.Count == 0)
            {
                logger.LogWarning("{UISymbol} No .csproj found next to the manifest; skipped setting WinAppRunUseExecutionAlias.", UiSymbols.Warning);
                return;
            }

            foreach (var csproj in csprojFiles)
            {
                var modified = await dotNetService.EnsureWinAppRunUseExecutionAliasAsync(csproj, cancellationToken);
                if (modified)
                {
                    logger.LogInformation("{UISymbol} Set WinAppRunUseExecutionAlias=true in {Csproj}", UiSymbols.Check, csproj.FullName);
                }
                else
                {
                    logger.LogInformation("{UISymbol} WinAppRunUseExecutionAlias is already enabled in {Csproj}", UiSymbols.Check, csproj.FullName);
                }
            }
        }
    }
}

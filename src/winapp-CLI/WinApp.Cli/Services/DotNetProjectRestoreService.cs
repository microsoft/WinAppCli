// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Restores a .NET project that <c>winapp init</c> configured. For .NET projects init records the SDK package
/// versions as <c>PackageReference</c> entries in the <c>.csproj</c> rather than in a <c>winapp.yaml</c>, so
/// there is nothing for the winapp.yaml-based restore to read and <c>dotnet restore</c> is what actually
/// restores them.
///
/// Split out of <see cref="WorkspaceSetupService"/> so that service is not extended with another distinct
/// responsibility (see the file-size guidance in AGENTS.md), and so this workflow can be tested on its own.
/// </summary>
internal sealed class DotNetProjectRestoreService(
    IDotNetService dotNetService,
    ILogger<DotNetProjectRestoreService> logger) : IDotNetProjectRestoreService
{
    /// <inheritdoc />
    public async Task<int> RestoreAsync(DirectoryInfo baseDirectory, DirectoryInfo configDir, CancellationToken cancellationToken = default)
    {
        // Every detected project is restored, deliberately without the interactive project picker `init` uses.
        // Restore is routinely run non-interactively (CI, or straight after a clone) and exposes no
        // project-selection option, so prompting here would block on redirected input; and unlike init — which
        // configures one project — restore is just reinstalling what is already declared, so doing that for
        // each project in the directory is both safe and what a multi-project repo needs.
        var csprojFiles = dotNetService.FindCsproj(baseDirectory);

        logger.LogInformation(
            "{UISymbol} .NET project detected with no winapp.yaml — SDK packages are PackageReferences in {Count} project(s). Running 'dotnet restore'.",
            UiSymbols.Note,
            csprojFiles.Count);

        foreach (var projectToRestore in csprojFiles)
        {
            // Let dotnet resolve nuget.config itself, relative to the project. That is the standard hierarchy
            // — the project's directory and its ancestors, merged with the user and machine levels — and it is
            // exactly what the user gets running `dotnet restore` by hand.
            //
            // Deliberately NOT forwarding the selected config directory as `--configfile`: that switch
            // replaces the whole hierarchy with one file, so a source declared there but authenticated through
            // credentials in the user-level config would start failing. Verified: restoring with
            // `--configfile` pointing at a config that lists one source drops every user-level source. Since
            // the config root cannot be honored without that loss, say so instead of silently restoring from
            // feeds the user did not select.
            if (!IsSameOrAncestorDirectory(configDir, projectToRestore.Directory!))
            {
                logger.LogWarning(
                    "{UISymbol} The selected configuration directory ({ConfigDir}) does not apply to {Project}: 'dotnet restore' resolves nuget.config relative to the project. Its own nuget.config hierarchy is used instead.",
                    UiSymbols.Warning,
                    configDir.FullName,
                    projectToRestore.Name);
            }

            var restoreExitCode = await dotNetService.RunDotnetInheritedAsync(
                projectToRestore.Directory!,
                $"restore \"{projectToRestore.FullName}\"",
                cancellationToken);

            if (restoreExitCode != 0)
            {
                logger.LogError(
                    "'dotnet restore' failed for {Project} (exit code {ExitCode}).",
                    projectToRestore.Name,
                    restoreExitCode);
                return 1;
            }

            logger.LogInformation("{UISymbol} Restore completed for {Project}.", UiSymbols.Check, projectToRestore.Name);
        }

        return 0;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="directory"/> itself or one of its ancestors.
    /// Used to tell whether a selected configuration directory is already part of the nuget.config hierarchy
    /// that <c>dotnet restore</c> discovers for a project, since that discovery walks up from the project.
    /// </summary>
    private static bool IsSameOrAncestorDirectory(DirectoryInfo candidate, DirectoryInfo directory)
    {
        var candidatePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.FullName));

        for (var current = directory; current is not null; current = current.Parent)
        {
            var currentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.FullName));
            if (string.Equals(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

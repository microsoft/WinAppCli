// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="IProjectRunService" />
internal sealed class ProjectRunService(
    IDotNetService dotNetService,
    IProjectDetectionService projectDetectionService,
    ICsWinRTMetadataShimService csWinRTMetadataShimService,
    IAnsiConsole ansiConsole,
    ILogger<ProjectRunService> logger) : IProjectRunService
{
    /// <summary>MSBuild properties requested from the evaluate step (always ≥2 → JSON output).</summary>
    private static readonly string[] RequestedProperties =
    [
        "TargetDir",
        "RunCommand",
        "WindowsPackageType",
        "WindowsAppSDKSelfContained",
        "EnableMsixTooling",
        "OutputType",
    ];

    /// <summary>Upper bound on build-output lines retained for the spinner failure dump (bounded tail).</summary>
    private const int MaxBuildTailLines = 500;

    /// <inheritdoc />
    public async Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken, string? projectSelector = null)
    {
        // Explicit file input: a .csproj (project mode) or a .sln/.slnx (solution mode).
        if (input is FileInfo file)
        {
            if (IsSolutionFile(file))
            {
                return await ResolveSolutionAsync(file, projectSelector, cancellationToken);
            }

            if (!string.Equals(file.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectRunException(
                    $"'{file.FullName}' is not a runnable input. Pass a .csproj, a .sln/.slnx solution, a directory containing one, or a build-output folder.");
            }

            var projectDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            return new RunInputResolution(WinAppRunMode.Project, file, projectDir);
        }

        var dir = (DirectoryInfo)input;

        // A solution in the directory wins over loose .csproj files: it carries the config→platform
        // map and defines $(SolutionDir), which some projects (e.g. those importing shared props via
        // $(SolutionDir)) need to build at all. Prefer it, matching what a developer opens in VS.
        List<FileInfo> solutions;
        try
        {
            solutions = dir.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly)
                .Concat(dir.EnumerateFiles("*.slnx", SearchOption.TopDirectoryOnly))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            solutions = [];
        }

        if (solutions.Count == 1)
        {
            return await ResolveSolutionAsync(solutions[0], projectSelector, cancellationToken);
        }

        if (solutions.Count > 1)
        {
            var slnNames = string.Join(", ", solutions.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            throw new ProjectRunException(
                $"Multiple solution files found in '{dir.FullName}' ({slnNames}). Specify which one to run, e.g. 'winapp run {solutions[0].Name}'.");
        }

        List<FileInfo> csprojs;
        try
        {
            csprojs = dir.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            csprojs = [];
        }

        // No top-level .csproj → folder mode (existing, unchanged behavior). Build-output folders
        // (bin/…) fall here. This path performs NO MSBuild evaluation, so folder mode stays identical.
        if (csprojs.Count == 0)
        {
            return new RunInputResolution(WinAppRunMode.Folder, null, dir);
        }

        if (csprojs.Count == 1)
        {
            return new RunInputResolution(WinAppRunMode.Project, csprojs[0], dir);
        }

        // A --project selector disambiguates directly without evaluation.
        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            var selected = MatchProjectSelector(csprojs, projectSelector, dir);
            if (selected is null)
            {
                var available = string.Join(", ", csprojs.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                throw new ProjectRunException(
                    $"--project '{projectSelector}' did not match a single .csproj in '{dir.FullName}'. Available: {available}.");
            }

            return new RunInputResolution(WinAppRunMode.Project, selected, dir);
        }

        // Multiple .csproj files — classify each via MSBuild evaluation so an executable/test project
        // is detected even when OutputType/IsTestProject come from an import (SDK defaults,
        // Directory.Build.props, the test SDK) rather than inline XML. A static parse cannot see those
        // and could silently pick the wrong project (spec M5). Evaluation falls back to the static
        // parse per-project when the SDK/restore is unavailable, so behavior never regresses.
        var executable = new List<FileInfo>();
        foreach (var csproj in csprojs)
        {
            if (await projectDetectionService.IsExecutableNonTestProjectAsync(csproj, dir, null, cancellationToken))
            {
                executable.Add(csproj);
            }
        }

        if (executable.Count == 1)
        {
            return new RunInputResolution(WinAppRunMode.Project, executable[0], dir);
        }

        // Zero or several runnable candidates → we cannot safely guess; require explicit selection.
        var names = string.Join(", ", csprojs.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        throw new ProjectRunException(
            $"Multiple .csproj files found in '{dir.FullName}' ({names}). Specify which project to run, e.g. 'winapp run {csprojs[0].Name}' or --project <name>.");
    }

    /// <summary>True when the file is a solution (<c>.sln</c> or the newer XML <c>.slnx</c>).</summary>
    private static bool IsSolutionFile(FileInfo file) =>
        string.Equals(file.Extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file.Extension, ".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the runnable app project out of a solution and records the solution on the result so
    /// the build defines <c>$(SolutionDir)</c>. The solution's project list comes from
    /// <c>dotnet sln &lt;sln&gt; list</c>; each candidate is classified with the same MSBuild
    /// evaluation used for a multi-<c>.csproj</c> directory. Exactly one launchable (non-test
    /// executable) project is required unless a matching <c>--project</c> selector is supplied.
    /// </summary>
    private async Task<RunInputResolution> ResolveSolutionAsync(FileInfo solution, string? projectSelector, CancellationToken cancellationToken)
    {
        var solutionDir = solution.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        var projects = await GetSolutionProjectsAsync(solution, solutionDir, cancellationToken);

        if (projects.Count == 0)
        {
            throw new ProjectRunException(
                $"No .csproj projects were found in '{solution.Name}'. 'winapp run' needs a runnable C# project in the solution.");
        }

        // An explicit --project selector short-circuits classification.
        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            var selected = MatchProjectSelector(projects, projectSelector, solutionDir);
            if (selected is null)
            {
                var available = string.Join(", ", projects.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                throw new ProjectRunException(
                    $"--project '{projectSelector}' did not match a single project in '{solution.Name}'. Available: {available}.");
            }

            return new RunInputResolution(WinAppRunMode.Project, selected, selected.Directory ?? solutionDir, solution);
        }

        var executable = new List<FileInfo>();
        var solutionProps = BuildSolutionPropertyTokens(solution);
        foreach (var project in projects)
        {
            if (await projectDetectionService.IsExecutableNonTestProjectAsync(project, solutionDir, solutionProps, cancellationToken))
            {
                executable.Add(project);
            }
        }

        if (executable.Count == 1)
        {
            var startup = executable[0];
            return new RunInputResolution(WinAppRunMode.Project, startup, startup.Directory ?? solutionDir, solution);
        }

        // Zero or several runnable app projects → we don't emulate VS's startup-project selection;
        // require an explicit --project so the wrong app is never launched behind the user's back.
        var candidates = (executable.Count > 0 ? executable : projects)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        var candidateList = string.Join(", ", candidates);
        var reason = executable.Count == 0
            ? $"No single runnable app project was found in '{solution.Name}'"
            : $"'{solution.Name}' contains multiple runnable app projects ({candidateList})";
        throw new ProjectRunException(
            $"{reason}. Specify which project to run with --project <name>. Projects: {candidateList}.");
    }

    /// <summary>
    /// Lists the C# projects in a solution via <c>dotnet sln &lt;sln&gt; list</c>, resolving each to an
    /// absolute <see cref="FileInfo"/>. Non-<c>.csproj</c> projects (e.g. <c>.vcxproj</c>) are excluded
    /// because <c>winapp run</c> builds and launches managed app projects.
    /// </summary>
    private async Task<List<FileInfo>> GetSolutionProjectsAsync(FileInfo solution, DirectoryInfo solutionDir, CancellationToken cancellationToken)
    {
        // Check for a capable SDK first: 'dotnet sln list' below also needs the SDK, and its failure
        // message ("could not read the solution") is far less actionable than the SDK guidance.
        var sdkError = await CheckSdkAsync(solutionDir, cancellationToken);
        if (sdkError != null)
        {
            throw new ProjectRunException(sdkError);
        }

        var arguments = WindowsCommandLine.JoinArguments(["sln", solution.FullName, "list"]) ?? string.Empty;

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(solutionDir, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new ProjectRunException(
                $"Could not read the solution '{solution.Name}' ('dotnet sln list' failed): {ex.Message}");
        }

        if (exitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            throw new ProjectRunException(
                $"Could not read the solution '{solution.Name}' ('dotnet sln list' exited {exitCode}). {detail}".TrimEnd());
        }

        var projects = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Skip the `dotnet sln list` header ("Project(s)" and its dashed underline).
            if (raw.All(c => c == '-') || string.Equals(raw, "Project(s)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!raw.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(solutionDir.FullName, raw));
            if (seen.Add(full))
            {
                projects.Add(new FileInfo(full));
            }
        }

        return projects;
    }

    /// <summary>
    /// Matches a <c>--project</c> selector against candidate projects by full path, file name (with or
    /// without the <c>.csproj</c> extension). Returns the single match, or null when zero or several
    /// candidates match (ambiguous).
    /// </summary>
    internal static FileInfo? MatchProjectSelector(IReadOnlyList<FileInfo> projects, string selector, DirectoryInfo baseDir)
    {
        var trimmed = selector.Trim();
        // Resolve a path-style selector against the input/solution directory (not the process cwd),
        // so `--project src/App/App.csproj` means "relative to what the user pointed winapp at".
        var rooted = Path.GetFullPath(trimmed, baseDir.FullName);
        var matches = projects.Where(p =>
            string.Equals(p.FullName, rooted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(p.Name), trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A path-based selector may not be rooted at the current directory; fall back to a name match
        // on the selector's leaf so `--project src/App/App.csproj` still resolves.
        if (matches.Count == 0)
        {
            var leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(leaf))
            {
                matches = projects.Where(p =>
                    string.Equals(p.Name, leaf, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(p.Name), Path.GetFileNameWithoutExtension(leaf), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <inheritdoc />
    public async Task<string?> CheckSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        const string upgradeHint =
            "Running csproj requires .NET SDK 8.0.100 or newer. Install or update it from https://aka.ms/dotnet/download.";

        int exitCode;
        string output;
        try
        {
            (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(workingDirectory, "--version", cancellationToken);
        }
        catch (Exception)
        {
            // dotnet not on PATH → Process.Start throws.
            return $"The .NET SDK was not found. {upgradeHint}";
        }

        if (exitCode != 0)
        {
            return $"Could not determine the .NET SDK version ('dotnet --version' failed). {upgradeHint}";
        }

        var versionLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(versionLine) && TryParseSdkVersion(versionLine, out var major, out var minor, out var patch))
        {
            var capable = major > 8 || (major == 8 && (minor > 0 || (minor == 0 && patch >= 100)));
            if (!capable)
            {
                return $"The .NET SDK {versionLine} is too old for project mode. {upgradeHint}";
            }
        }

        // Present but unparseable version → assume a modern SDK; the build will surface a real error
        // if --getProperty is genuinely unsupported.
        return null;
    }

    /// <summary>
    /// SHIM (temporary): resolves the <c>CsWinRTWindowsMetadata</c> folder to inject for SDK-less builds,
    /// or <c>null</c> when the user already set the property (their value wins) or no injection is
    /// needed/possible. See <see cref="CsWinRTMetadataShimService"/>.
    /// </summary>
    private string? ResolveCsWinRTMetadataShim(ProjectRunOptions options)
    {
        var userSetMetadata = options.Properties.Any(p =>
            p.StartsWith("CsWinRTWindowsMetadata=", StringComparison.OrdinalIgnoreCase));
        if (userSetMetadata)
        {
            return null;
        }

        return csWinRTMetadataShimService.ResolveMetadataFolder(options.Framework);
    }

    /// <inheritdoc />
    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

        // SHIM (temporary): on hosts with no registered Windows SDK, resolve a folder of ref-pack winmds
        // to inject as -p:CsWinRTWindowsMetadata so C#/WinRT authoring projects build (see
        // CsWinRTMetadataShimService). Skipped when the user already set the property. null = no injection.
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options);

        // Two passes (spec §8.2, Change #1): (1) BUILD — a plain `dotnet build` whose console log
        // STREAMS live so the user sees progress (skipped under --no-build); then (2) EVALUATE — a
        // fast `dotnet msbuild --getProperty` that returns the resolved output paths as JSON. The
        // split is required because `--getProperty` SUPPRESSES normal MSBuild console output, so a
        // single combined pass would build silently. The evaluate pass is fed the SAME effective
        // Configuration/RID/Platform/TFM/-p as the build so its TargetDir/RunCommand match what was
        // actually built.
        if (!options.NoBuild)
        {
            var useLiveSpinner = ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);
            var buildExit = await RunBuildPassAsync(csproj, options, workingDir, useLiveSpinner, csWinRTMetadata, cancellationToken);
            if (buildExit != 0)
            {
                // dotnet's diagnostics were already streamed live (or dumped on the spinner-failure
                // path); just log the summary and propagate the exit code — do not attempt to launch.
                logger.LogError("{UISymbol} Build failed for {Project} (exit code {ExitCode}).", UiSymbols.Error, csproj.Name, buildExit);
                return new ProjectBuildOutcome(null, buildExit);
            }
        }

        var evaluateArgs = BuildEvaluateArguments(csproj, options, csWinRTMetadata);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, evaluateArgs);

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);

        if (exitCode != 0)
        {
            // The build (if any) succeeded but property evaluation failed — surface dotnet's
            // diagnostics and propagate the exit code rather than launch against unknown output.
            logger.LogError("{UISymbol} Could not evaluate project properties for {Project} (exit code {ExitCode}).", UiSymbols.Error, csproj.Name, exitCode);
            var combined = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            if (!string.IsNullOrWhiteSpace(combined))
            {
                // Keep stdout clean for --json consumers; route diagnostics to stderr instead.
                if (options.Json)
                {
                    Console.Error.WriteLine(combined);
                }
                else
                {
                    ansiConsole.WriteLine(combined);
                }
            }

            return new ProjectBuildOutcome(null, exitCode);
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);

        var outputType = GetProp(props, "OutputType");
        if (!string.IsNullOrEmpty(outputType) &&
            !string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectRunException(
                $"'{csproj.Name}' is not a runnable project (OutputType='{outputType}'). 'winapp run' requires an executable project (OutputType Exe or WinExe).");
        }

        var targetDir = GetProp(props, "TargetDir");
        var runCommand = GetProp(props, "RunCommand");
        var selfContained = string.Equals(GetProp(props, "WindowsAppSDKSelfContained"), "true", StringComparison.OrdinalIgnoreCase);
        var packaging = DeterminePackaging(props, targetDir);

        if (string.IsNullOrEmpty(targetDir))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory (TargetDir) for '{csproj.Name}'. Ensure the project builds successfully.");
        }

        if (packaging == ProjectPackaging.Unpackaged)
        {
            if (string.IsNullOrEmpty(runCommand) || !File.Exists(runCommand))
            {
                var reason = options.NoBuild
                    ? "The runnable executable was not found. Remove --no-build so the project is built first, or build it manually."
                    : "The build did not produce a runnable executable (RunCommand).";
                throw new ProjectRunException(
                    $"'{csproj.Name}' resolves to an unpackaged app but no launchable .exe is available. {reason}");
            }
        }

        var resolution = new ProjectRunResolution(
            csproj,
            targetDir,
            string.IsNullOrEmpty(runCommand) ? null : runCommand,
            packaging,
            selfContained,
            options.Architecture);

        return new ProjectBuildOutcome(resolution, 0);
    }

    /// <summary>
    /// Determines packaged vs unpackaged from the evaluated properties (spec §7.1), never from
    /// manifest presence.
    /// </summary>
    private static ProjectPackaging DeterminePackaging(IReadOnlyDictionary<string, string> props, string targetDir)
    {
        var windowsPackageType = GetProp(props, "WindowsPackageType");

        if (string.Equals(windowsPackageType, "None", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Unpackaged;
        }

        if (!string.IsNullOrEmpty(windowsPackageType))
        {
            // MSIX (or any other non-empty value) → packaged.
            return ProjectPackaging.Packaged;
        }

        // Unset/empty (common on the --no-build evaluate-only path, where MSIX targets don't run):
        // fall back to EnableMsixTooling or an emitted recipe.
        if (string.Equals(GetProp(props, "EnableMsixTooling"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Packaged;
        }

        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            try
            {
                if (Directory.EnumerateFiles(targetDir, "*.build.appxrecipe").Any())
                {
                    return ProjectPackaging.Packaged;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Ignore — fall through to unpackaged.
            }
        }

        return ProjectPackaging.Unpackaged;
    }

    /// <summary>
    /// Builds the argument string for the project-mode BUILD pass: a plain <c>dotnet build</c> that
    /// produces the output and STREAMS its console log. It deliberately omits <c>--getProperty</c>
    /// (which suppresses that log) and needs no explicit <c>-t:Build</c> (Build is the default
    /// target). The dedicated <c>-c</c>/<c>-r</c>/<c>-f</c> switches always beat a same-named user
    /// <c>-p</c>; Platform is derived from <c>--arch</c> only when the user didn't set it;
    /// <c>EnableDynamicPlatformResolution</c> is enabled so a forced global Platform doesn't leak into
    /// P2P references (multi-project apps, CS0006) unless the user set it explicitly; and the
    /// <c>-v</c> verbosity is mapped from the CLI's log level (Change #1, spec §8.3/§8.5).
    /// </summary>
    internal static string BuildBuildPassArguments(FileInfo csproj, ProjectRunOptions options, string verbosity, string? csWinRTMetadataFolder = null)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);
        var platform = RunArchHelper.ToPlatform(options.Architecture);
        var userSpecifiesPlatform = options.Properties.Any(p => p.StartsWith("Platform=", StringComparison.OrdinalIgnoreCase));
        var userSpecifiesEdpr = options.Properties.Any(p => p.StartsWith("EnableDynamicPlatformResolution=", StringComparison.OrdinalIgnoreCase));

        var tokens = new List<string>
        {
            "build",
            csproj.FullName,
            "-c",
            options.Configuration,
            "-r",
            rid,
        };

        if (options.NoRestore)
        {
            tokens.Add("--no-restore");
        }

        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add("-f");
            tokens.Add(options.Framework);
        }

        tokens.Add("-v");
        tokens.Add(verbosity);

        // User -p properties come FIRST; the dedicated -c/-r/-f switches above always beat a
        // same-named -p (see WarnOnOverriddenFlags).
        foreach (var property in options.Properties)
        {
            tokens.Add($"-p:{property}");
        }

        // When the target was resolved from a solution, define $(SolutionDir) and its siblings so
        // projects that reference them build exactly as they do under `dotnet build <sln>` / VS.
        AppendSolutionProperties(tokens, options);

        // Derived Platform only when the user didn't specify one (a user -p:Platform wins, spec R2-L2).
        if (!userSpecifiesPlatform)
        {
            tokens.Add($"-p:Platform={platform}");
        }

        // Negotiate each ProjectReference's own platform instead of leaking the forced global Platform
        // into them. A global -p:Platform=<arch> (forced above, or supplied by the user) otherwise flows
        // into AnyCPU/netstandard2.0 P2P references, so a multi-project app resolves its reference to the
        // AnyCPU output path while the reference was built under bin\<arch>\... → CS0006 "metadata file
        // could not be found". EnableDynamicPlatformResolution (MSBuild platform negotiation, .NET 6+)
        // does automatically what a .sln's ProjectConfigurationPlatforms map does by hand; it is a no-op
        // for single-project apps and does not change the app's own TargetDir. Suppressed when the user
        // set it explicitly so an intentional project/user value is respected.
        if (!userSpecifiesEdpr)
        {
            tokens.Add("-p:EnableDynamicPlatformResolution=true");
        }

        // SHIM (temporary): inject the resolved ref-pack winmd folder so cswinrt.exe can find contract
        // winmds without a registered Windows SDK. Only present when the shim resolved a folder (SDK
        // absent + ref pack restored) and the user didn't set the property. See CsWinRTMetadataShimService.
        if (!string.IsNullOrEmpty(csWinRTMetadataFolder))
        {
            tokens.Add($"-p:CsWinRTWindowsMetadata={csWinRTMetadataFolder}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the argument string for the project-mode EVALUATE pass: a fast, side-effect-free
    /// <c>dotnet msbuild --getProperty</c> that returns the resolved output paths as JSON. It is the
    /// same shape used on the <c>--no-build</c> path and is fed the SAME effective build inputs as the
    /// build pass so its <c>TargetDir</c>/<c>RunCommand</c> match what was built. <c>dotnet msbuild</c>
    /// rejects <c>-c</c>/<c>-r</c> (MSB1001), so Configuration/RID/TFM are passed as <c>-p:</c> and are
    /// emitted LAST so MSBuild's last-wins makes a dedicated value beat a conflicting user <c>-p</c>
    /// (spec §8.2/M2).
    /// </summary>
    internal static string BuildEvaluateArguments(FileInfo csproj, ProjectRunOptions options, string? csWinRTMetadataFolder = null)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);
        var platform = RunArchHelper.ToPlatform(options.Architecture);
        var userSpecifiesPlatform = options.Properties.Any(p => p.StartsWith("Platform=", StringComparison.OrdinalIgnoreCase));
        var userSpecifiesEdpr = options.Properties.Any(p => p.StartsWith("EnableDynamicPlatformResolution=", StringComparison.OrdinalIgnoreCase));

        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };

        // User -p first so the dedicated equivalents below win on a conflict (MSBuild is last-wins).
        foreach (var property in options.Properties)
        {
            tokens.Add($"-p:{property}");
        }

        // Match the build pass: define $(SolutionDir) & siblings so the evaluated TargetDir/RunCommand
        // resolve against the same solution-anchored inputs as the build (solution mode only).
        AppendSolutionProperties(tokens, options);

        tokens.Add($"-p:Configuration={options.Configuration}");
        tokens.Add($"-p:RuntimeIdentifier={rid}");
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add($"-p:TargetFramework={options.Framework}");
        }

        if (!userSpecifiesPlatform)
        {
            tokens.Add($"-p:Platform={platform}");
        }

        // Keep the evaluate pass's project graph identical to the build pass so TargetDir/RunCommand
        // resolve against the same P2P references. See BuildBuildPassArguments for the full rationale
        // (forced global Platform leaks into AnyCPU/netstandard2.0 references → CS0006 without this).
        // EDPR doesn't change the app's own TargetDir, so it is safe to add on the evaluate/--no-build
        // path too. Suppressed when the user set it explicitly.
        if (!userSpecifiesEdpr)
        {
            tokens.Add("-p:EnableDynamicPlatformResolution=true");
        }

        // SHIM (temporary): keep the evaluate pass's inputs identical to the build pass — inject the same
        // CsWinRTWindowsMetadata folder when the shim resolved one. See CsWinRTMetadataShimService.
        if (!string.IsNullOrEmpty(csWinRTMetadataFolder))
        {
            tokens.Add($"-p:CsWinRTWindowsMetadata={csWinRTMetadataFolder}");
        }

        foreach (var name in RequestedProperties)
        {
            tokens.Add($"--getProperty:{name}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Appends the <c>Solution*</c> MSBuild properties a solution build normally sets — most
    /// importantly <c>$(SolutionDir)</c> — when the run target was resolved from a solution. Building
    /// a bare <c>.csproj</c> leaves these undefined, so projects that reference them (shared prop
    /// imports, output paths) fail; defining them here builds the project the same way it builds under
    /// <c>dotnet build &lt;sln&gt;</c> / Visual Studio. No-op for a bare <c>.csproj</c> target.
    /// </summary>
    private static void AppendSolutionProperties(List<string> tokens, ProjectRunOptions options)
    {
        if (options.Solution is not { } solution)
        {
            return;
        }

        tokens.AddRange(BuildSolutionPropertyTokens(solution));
    }

    /// <summary>
    /// Builds the <c>-p:Solution*</c> MSBuild property tokens a solution build normally sets — most
    /// importantly <c>$(SolutionDir)</c> (trailing separator, per MSBuild convention). Shared by the
    /// build pass, the evaluation pass, and project classification so all three see the same
    /// solution-defined properties.
    /// </summary>
    private static IReadOnlyList<string> BuildSolutionPropertyTokens(FileInfo solution)
    {
        var solutionDir = solution.Directory?.FullName ?? Directory.GetCurrentDirectory();
        // MSBuild's $(SolutionDir) convention is a trailing directory separator. EscapeArgument
        // doubles a trailing backslash before a closing quote, so a quoted value round-trips exactly.
        if (!solutionDir.EndsWith(Path.DirectorySeparatorChar) && !solutionDir.EndsWith(Path.AltDirectorySeparatorChar))
        {
            solutionDir += Path.DirectorySeparatorChar;
        }

        var solutionName = Path.GetFileNameWithoutExtension(solution.Name);

        return
        [
            $"-p:SolutionDir={solutionDir}",
            $"-p:SolutionPath={solution.FullName}",
            $"-p:SolutionName={solutionName}",
            $"-p:SolutionFileName={solution.Name}",
            $"-p:SolutionExt={solution.Extension}",
        ];
    }
    /// <list type="bullet">
    ///   <item><c>--json</c>: stream to stderr only so stdout stays pure JSON — no banner, no spinner.</item>
    ///   <item>Interactive terminal, non-verbose: animate a Spectre status spinner and hide the raw
    ///   build lines; on failure, dump the captured output so the MSBuild error is visible.</item>
    ///   <item>Otherwise (verbose, or an agent/CI/redirected terminal): print a single "Building…"
    ///   line and stream dotnet's output live (plain lines — no spinner-frame flooding).</item>
    /// </list>
    /// </summary>
    internal async Task<int> RunBuildPassAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        bool useLiveSpinner,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);
        var buildArgs = BuildBuildPassArguments(csproj, options, verbosity, csWinRTMetadataFolder);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, buildArgs);

        var banner = $"Building {csproj.Name} ({options.Configuration} | {options.Architecture})...";

        // --json: stdout must stay pure JSON, so route ALL build output to stderr and show no banner.
        // Console.Error is synchronized, so the concurrent stdout/stderr callbacks are safe.
        if (options.Json)
        {
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, buildArgs,
                onOutputLine: static line => Console.Error.WriteLine(line),
                onErrorLine: static line => Console.Error.WriteLine(line),
                cancellationToken);
        }

        // Interactive human, non-verbose: animate a spinner and keep the raw build lines hidden,
        // revealing the (bounded) captured output only if the build fails.
        if (useLiveSpinner && !logger.IsEnabled(LogLevel.Debug))
        {
            var captured = new List<string>();
            void Capture(string line)
            {
                lock (captured)
                {
                    captured.Add(line);
                    if (captured.Count > MaxBuildTailLines)
                    {
                        captured.RemoveAt(0);
                    }
                }
            }

            var spinnerExit = await ansiConsole.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync(banner, async _ =>
                    await dotNetService.RunDotnetStreamingAsync(
                        workingDir, buildArgs, Capture, Capture, cancellationToken));

            if (spinnerExit != 0)
            {
                foreach (var line in captured)
                {
                    ansiConsole.WriteLine(line);
                }
            }

            return spinnerExit;
        }

        // Verbose, or a non-interactive/agent/CI terminal: a single static line + live streamed output.
        // Serialize the writes so the concurrent stdout/stderr callbacks don't interleave.
        //
        // --quiet (Information suppressed) must keep stdout clean like --json: skip the banner and
        // route build output to stderr so failures stay visible without polluting stdout.
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, buildArgs,
                onOutputLine: static line => Console.Error.WriteLine(line),
                onErrorLine: static line => Console.Error.WriteLine(line),
                cancellationToken);
        }

        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} {banner}");
        var writeLock = new object();
        void WriteLive(string line)
        {
            lock (writeLock)
            {
                ansiConsole.WriteLine(line);
            }
        }

        return await dotNetService.RunDotnetStreamingAsync(
            workingDir, buildArgs, WriteLive, WriteLive, cancellationToken);
    }

    /// <summary>
    /// Maps the CLI's effective log level to a dotnet <c>-v</c> verbosity for the build pass so that
    /// <c>--verbose</c> reaches dotnet (Change #1): trace ⇒ detailed, verbose ⇒ normal, <c>--quiet</c>
    /// ⇒ quiet; otherwise minimal to keep ordinary runs tidy.
    /// </summary>
    private static string ResolveBuildVerbosity(ILogger logger, bool json)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            return "detailed";
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            return "normal";
        }

        // --quiet suppresses Information (and is never combined with --json); keep dotnet quiet too.
        if (!json && !logger.IsEnabled(LogLevel.Information))
        {
            return "quiet";
        }

        return "minimal";
    }

    private void WarnOnOverriddenFlags(ProjectRunOptions options)
    {
        // Match dotnet's behavior (dedicated flag wins over a same-named -p) but leave a debug trail.
        foreach (var property in options.Properties)
        {
            var name = property.Split('=', 2)[0].Trim();
            if (name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "{UISymbol} -p:{Property} is overridden by the dedicated flag (matches dotnet precedence).",
                    UiSymbols.Note, property);
            }
            else if (name.Equals("Platform", StringComparison.OrdinalIgnoreCase))
            {
                // Opposite precedence to Configuration/RID (spec R2-L2): a user -p:Platform WINS over the
                // --arch-derived Platform (which is suppressed). The RuntimeIdentifier still follows
                // --arch, so an inconsistent pair (e.g. --arch x86 -p:Platform=ARM64) builds a mismatched
                // app — warn so the divergence isn't silent.
                logger.LogDebug(
                    "{UISymbol} -p:{Property} overrides the --arch-derived Platform; the RuntimeIdentifier still follows --arch, so ensure they are consistent.",
                    UiSymbols.Note, property);
            }
        }
    }

    private static string GetProp(IReadOnlyDictionary<string, string> props, string name)
        => props.TryGetValue(name, out var value) ? value.Trim() : string.Empty;

    /// <summary>
    /// Parses the leading <c>major.minor.patch</c> of a <c>dotnet --version</c> string
    /// (e.g. <c>8.0.100</c>, <c>10.0.301</c>, <c>8.0.100-preview.1</c>).
    /// </summary>
    internal static bool TryParseSdkVersion(string versionText, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        // Strip any prerelease/build suffix.
        var core = versionText.Trim();
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        return int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor)
            && int.TryParse(parts[2], out patch);
    }
}

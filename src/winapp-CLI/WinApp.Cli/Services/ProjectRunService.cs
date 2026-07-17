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
    public async Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken)
    {
        // Explicit file input: must be a .csproj (the unambiguous project-mode form).
        if (input is FileInfo file)
        {
            if (!string.Equals(file.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectRunException(
                    $"'{file.FullName}' is not a .csproj file. Pass a .csproj, a directory containing one, or a build-output folder.");
            }

            var projectDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            return new RunInputResolution(WinAppRunMode.Project, file, projectDir);
        }

        var dir = (DirectoryInfo)input;
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

        // Multiple .csproj files — classify each via MSBuild evaluation so an executable/test project
        // is detected even when OutputType/IsTestProject come from an import (SDK defaults,
        // Directory.Build.props, the test SDK) rather than inline XML. A static parse cannot see those
        // and could silently pick the wrong project (spec M5). Evaluation falls back to the static
        // parse per-project when the SDK/restore is unavailable, so behavior never regresses.
        var executable = new List<FileInfo>();
        foreach (var csproj in csprojs)
        {
            if (await IsExecutableNonTestProjectAsync(csproj, dir, cancellationToken))
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
            $"Multiple .csproj files found in '{dir.FullName}' ({names}). Specify which project to run, e.g. 'winapp run {csprojs[0].Name}'.");
    }

    /// <summary>
    /// Classifies a candidate project as a runnable non-test executable, preferring an MSBuild
    /// evaluation of <c>OutputType</c>/<c>IsTestProject</c> (which honors imports) and falling back
    /// to the static XML parse when evaluation is unavailable (no capable SDK, project not restored).
    /// </summary>
    private async Task<bool> IsExecutableNonTestProjectAsync(FileInfo csproj, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        // Evaluate-only (no -t:Build): fast and side-effect free. Unlike a build, we only read
        // static-ish properties, so a stale/absent output is irrelevant here.
        var arguments = WindowsCommandLine.JoinArguments(
        [
            "msbuild",
            csproj.FullName,
            "--getProperty:OutputType",
            "--getProperty:IsTestProject",
        ]) ?? string.Empty;

        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDirectory, arguments, cancellationToken);
            if (exitCode == 0)
            {
                var props = MsBuildPropertyReader.Parse(stdout, ["OutputType", "IsTestProject"]);
                if (props.Count > 0)
                {
                    var outputType = GetProp(props, "OutputType");
                    var isTest = string.Equals(GetProp(props, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase);
                    var isExecutable = string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
                    return isExecutable && !isTest;
                }
            }

            logger.LogDebug("{UISymbol} Could not evaluate {Project} for disambiguation; falling back to static parse.", UiSymbols.Note, csproj.Name);
        }
        catch (Exception ex)
        {
            // dotnet not on PATH / evaluation failed → fall back to the static parse below.
            logger.LogDebug("{UISymbol} Evaluation of {Project} failed ({Message}); falling back to static parse.", UiSymbols.Note, csproj.Name, ex.Message);
        }

        return ProjectDetectionService.IsExecutableNonTestProject(csproj);
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

    /// <inheritdoc />
    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

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
            var buildExit = await RunBuildPassAsync(csproj, options, workingDir, useLiveSpinner, cancellationToken);
            if (buildExit != 0)
            {
                // dotnet's diagnostics were already streamed live (or dumped on the spinner-failure
                // path); just log the summary and propagate the exit code — do not attempt to launch.
                logger.LogError("{UISymbol} Build failed for {Project} (exit code {ExitCode}).", UiSymbols.Error, csproj.Name, buildExit);
                return new ProjectBuildOutcome(null, buildExit);
            }
        }

        var evaluateArgs = BuildEvaluateArguments(csproj, options);
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
    internal static string BuildBuildPassArguments(FileInfo csproj, ProjectRunOptions options, string verbosity)
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
    internal static string BuildEvaluateArguments(FileInfo csproj, ProjectRunOptions options)
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

        foreach (var name in RequestedProperties)
        {
            tokens.Add($"--getProperty:{name}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Runs the project-mode BUILD pass, streaming dotnet's output according to the environment
    /// (Change #1 + Change #4):
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
        CancellationToken cancellationToken)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);
        var buildArgs = BuildBuildPassArguments(csproj, options, verbosity);
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

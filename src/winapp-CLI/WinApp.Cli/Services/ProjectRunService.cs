// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="IProjectRunService" />
internal sealed partial class ProjectRunService(
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
        "_WinAppRunSupportActive",
        "OutputType",
    ];

    /// <summary>
    /// Property names owned by a dedicated <c>-c</c>/<c>-r</c>/<c>-f</c> switch. A same-named user
    /// <c>-p</c> is dropped from BOTH passes so they can't resolve a different Configuration/RID/TFM (see
    /// <see cref="WarnOnOverriddenFlags"/>). <c>Platform</c> is intentionally NOT reserved — project mode
    /// forwards it as-is and conveys arch via the RuntimeIdentifier only. <c>Configuration</c>/
    /// <c>RuntimeIdentifier</c> are always pinned; <c>TargetFramework</c> only when a TFM is resolved (a
    /// bare <c>-p:TargetFramework</c> is promoted and re-emitted via <c>-f</c>, so dropping it is safe).
    /// </summary>
    private static readonly string[] DedicatedFlagProperties = ["Configuration", "RuntimeIdentifier", "TargetFramework"];

    /// <summary>
    /// Test seam for the "real interactive terminal" gate <see cref="RunBuildPassAsync"/> uses to choose
    /// the native terminal-logger launcher over plain line streaming. <see langword="null"/> in production
    /// (the gate is <see cref="ProgressDisplay.ShouldUseLiveSpinner(IAnsiConsole, ILogger)"/>); overridable
    /// only because that gate reads process-global state that is always false under the test host.
    /// </summary>
    internal Func<bool>? NativeTerminalGateOverrideForTests { get; set; }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Honor Ctrl+C during the SDK probe instead of reporting it as a missing SDK.
            throw;
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
    private string? ResolveCsWinRTMetadataShim(ProjectRunOptions options, string? framework)
    {
        if (UserSetCsWinRTMetadata(options))
        {
            return null;
        }

        return csWinRTMetadataShimService.ResolveMetadataFolder(framework);
    }

    /// <summary>
    /// True when the user supplied their own <c>-p:CsWinRTWindowsMetadata=…</c>; their value wins and the
    /// shim must not inject (or trigger a restore to resolve) anything.
    /// </summary>
    private static bool UserSetCsWinRTMetadata(ProjectRunOptions options) =>
        options.Properties.Any(p =>
            p.StartsWith("CsWinRTWindowsMetadata=", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the silent pre-build steps — effective-framework pinning, the CsWinRT metadata shim, and the
    /// pre-build restore (whole-solution when applicable) — before the build pass. These shell out to
    /// buffered <c>dotnet</c> sub-processes with no console output, so <paramref name="setStatus"/> lets an
    /// interactive caller animate a spinner; pass <see langword="null"/> to run silently (agents / <c>--json</c>
    /// / <c>--quiet</c> / <c>--verbose</c>). Returns the
    /// (possibly framework-pinned) options, the build-pass options (with <c>NoRestore</c> set when a
    /// pre-restore already covered the target), and the resolved CsWinRT metadata folder (or null).
    /// </summary>
    private async Task<(ProjectRunOptions Options, ProjectRunOptions BuildOptions, string? CsWinRTMetadata)>
        PrepareBuildInputsAsync(
            FileInfo csproj,
            ProjectRunOptions options,
            DirectoryInfo workingDir,
            Action<string>? setStatus,
            CancellationToken cancellationToken)
    {
        // Pin an effective single TFM for a multi-targeted project (default = first declared) BEFORE any
        // pass so build/evaluate/packaging/provisioning all agree. No-op when single-targeted / --framework set.
        setStatus?.Invoke("Resolving project...");
        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);

        // The shim needs the effective single TFM to steer ref-pack selection on SDK-less hosts. Resolved
        // separately since options.Framework stays null for a normal single-targeted project; never used as
        // a build -p:TargetFramework.
        var shimFramework = await ResolveShimFrameworkAsync(csproj, options, workingDir, cancellationToken);

        // SHIM (temporary): on hosts with no registered Windows SDK, resolve ref-pack winmds to inject as
        // -p:CsWinRTWindowsMetadata. Skipped when the user set the property. null = no injection.
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options, shimFramework);
        var buildOptions = options;

        // When the target lives in a solution, restore the whole solution's managed projects up front so
        // build-dependency siblings that aren't ProjectReferences (e.g. a COM server) have project.assets.json
        // (else NETSDK1004) — matching VS / `dotnet build <sln>`. Gated on actually building + restore not opted out.
        if (!options.NoBuild && !options.NoRestore)
        {
            setStatus?.Invoke("Restoring dependencies...");

            // (1) Restore the owning solution's managed siblings. An all-managed whole-solution restore also
            // covered the target, so the passes below can skip their own restore.
            var restoredWholeSolution = await RestoreSolutionSiblingsAsync(csproj, options, workingDir, cancellationToken);

            // (2) SHIM (temporary): on a clean SDK-less host the ref pack may not be on disk when the shim
            // first resolves, so it no-ops and the first build fails even though it restores the ref pack.
            // Pre-populate it with an explicit restore, then re-resolve. Only when the shim would inject.
            if (csWinRTMetadata is null
                && !UserSetCsWinRTMetadata(options)
                && csWinRTMetadataShimService.IsWindowsSdkAbsent())
            {
                var restoreExit = restoredWholeSolution
                    ? 0
                    : await RunRestorePassAsync(csproj, options, workingDir, cancellationToken);
                if (restoreExit == 0)
                {
                    csWinRTMetadata = ResolveCsWinRTMetadataShim(options, shimFramework);
                    buildOptions = options with { NoRestore = true }; // Explicit restore done; skip build-pass restore.
                }
            }
            else if (restoredWholeSolution)
            {
                buildOptions = options with { NoRestore = true }; // Whole-solution restore covered the target.
            }
        }

        return (options, buildOptions, csWinRTMetadata);
    }

    /// <inheritdoc />
    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

        // The steps below shell out to SILENT (buffered) dotnet sub-processes — a whole-solution restore
        // alone can take several seconds with no output. On a real interactive terminal, animate a spinner
        // so the user sees liveness before the build line. Skipped under --verbose (traces render plainly)
        // and under --json/--quiet/agent/CI/redirected (ShouldUseLiveSpinner == false).
        ProjectRunOptions buildOptions;
        string? csWinRTMetadata;
        if (ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger) && !logger.IsEnabled(LogLevel.Debug))
        {
            (options, buildOptions, csWinRTMetadata) = await ansiConsole.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Resolving project...", async ctx =>
                    await PrepareBuildInputsAsync(csproj, options, workingDir, s => ctx.Status(s), cancellationToken));
        }
        else
        {
            (options, buildOptions, csWinRTMetadata) = await PrepareBuildInputsAsync(
                csproj, options, workingDir, setStatus: null, cancellationToken);
        }

        // Two passes (Change #1): (1) BUILD — a plain `dotnet build` whose console log
        // STREAMS live so the user sees progress (skipped under --no-build); then (2) EVALUATE — a
        // fast `dotnet msbuild --getProperty` that returns the resolved output paths as JSON. The
        // split is required because `--getProperty` SUPPRESSES normal MSBuild console output, so a
        // single combined pass would build silently. The evaluate pass is fed the SAME effective
        // Configuration/RID/Platform/TFM/-p as the build so its TargetDir/RunCommand match what was
        // actually built.
        if (!options.NoBuild)
        {
            var buildExit = await RunBuildPassAsync(csproj, buildOptions, workingDir, csWinRTMetadata, cancellationToken);
            if (buildExit != 0)
            {
                // dotnet's diagnostics were already streamed live; log the summary and propagate the exit code.
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
            options.Architecture,
            options.Framework,
            options.NoRestore);

        return new ProjectBuildOutcome(resolution, 0);
    }

    /// <inheritdoc />
    public async Task<bool> IsDefinitivelyUnpackagedAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        // Pin the same effective TFM the real build/evaluate passes use, so a multi-targeted project
        // is probed against a single-TFM inner build, not the empty cross-targeting outer node.
        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);
        var shimFramework = await ResolveShimFrameworkAsync(csproj, options, workingDir, cancellationToken);
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options, shimFramework);

        // Reuse the exact evaluate pass (same -p/RID/TFM/shim as a real build) so the WindowsPackageType we
        // read matches what the build would see. Evaluate-only — no build is triggered.
        var evaluateArgs = BuildEvaluateArguments(csproj, options, csWinRTMetadata);
        int exitCode;
        string stdout;
        try
        {
            (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Starting/communicating with dotnet failed → indeterminate; let the authoritative build classify.
            return false;
        }

        if (exitCode != 0)
        {
            // Evaluation failed → indeterminate; let the authoritative build classify.
            return false;
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);

        // Only an EXPLICIT WindowsPackageType=None is definitive. An unset value is NOT — a packaged app
        // declaring identity via an emitted recipe also evaluates empty here pre-build, so
        // DeterminePackaging's post-build recipe fallback stays authoritative.
        return string.Equals(GetProp(props, "WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines packaged vs unpackaged from the evaluated properties, never from
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
        // fall back to EnableMsixTooling, the WinApp run-support gate, or an emitted recipe.
        if (string.Equals(GetProp(props, "EnableMsixTooling"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Packaged;
        }

        // The Microsoft.Windows.SDK.BuildTools.WinApp integration activates run support for an executable
        // Windows project that ships an appxmanifest.xml but sets no WindowsPackageType (e.g.
        // samples/dotnet-app). Those run WITH identity off the copied manifest, so honor the signal rather
        // than launching the apphost without identity (which breaks Package.Current).
        if (string.Equals(GetProp(props, "_WinAppRunSupportActive"), "true", StringComparison.OrdinalIgnoreCase))
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
    /// SHIM (temporary): runs an explicit <c>dotnet restore</c> so the <c>Microsoft.Windows.SDK.NET.Ref</c>
    /// ref pack lands on disk BEFORE the shim resolves its winmd folder (fixes the clean-host first-build
    /// failure). Returns the dotnet exit code so the caller only skips the build's own restore on success.
    /// </summary>
    private async Task<int> RunRestorePassAsync(FileInfo csproj, ProjectRunOptions options, DirectoryInfo workingDir, CancellationToken cancellationToken)
    {
        var restoreArgs = BuildRestorePassArguments(csproj, options);
        logger.LogDebug("{UISymbol} Restoring before SDK-less CsWinRT metadata resolution: dotnet {Arguments}", UiSymbols.Note, restoreArgs);
        var (exitCode, _, _) = await dotNetService.RunDotnetCommandAsync(workingDir, restoreArgs, cancellationToken);
        if (exitCode != 0)
        {
            // Non-fatal: fall through and let the build pass restore + surface any real error itself.
            logger.LogDebug("{UISymbol} Pre-shim restore exited {ExitCode}; deferring to the build pass.", UiSymbols.Note, exitCode);
        }
        return exitCode;
    }

    /// <summary>
    /// Restores the owning solution's managed sibling projects before the target build so build-dependency
    /// siblings that aren't <c>ProjectReference</c>s still have a <c>project.assets.json</c> (NETSDK1004
    /// parity with VS / <c>dotnet build &lt;sln&gt;</c>). Only fires for a solution-resolved run. When every
    /// listed project is managed, a single <c>dotnet restore &lt;sln&gt;</c> covers the whole graph and this
    /// returns <see langword="true"/> (caller skips the build pass's restore). When a native project is
    /// present (<c>dotnet restore</c> can't handle it VS-less) OR the whole-solution restore fails, managed
    /// siblings are restored individually and this returns <see langword="false"/>. All restores are
    /// best-effort; the build pass surfaces any real error.
    /// </summary>
    private async Task<bool> RestoreSolutionSiblingsAsync(FileInfo target, ProjectRunOptions options, DirectoryInfo workingDir, CancellationToken cancellationToken)
    {
        if (options.Solution is null)
        {
            return false;
        }

        var (allManaged, siblings) = ComputeSolutionRestorePlan(options.Solution, target);
        if (siblings.Count == 0)
        {
            // Solution lists only the target (or only native siblings) — nothing extra to restore; the
            // normal target restore is unchanged.
            return false;
        }

        if (allManaged)
        {
            // Closest to VS: one restore over the whole solution pulls the target and every sibling.
            var args = BuildRestorePassArguments(options.Solution, options);
            logger.LogDebug("{UISymbol} Restoring solution before build (build-dependency parity): dotnet {Arguments}", UiSymbols.Note, args);
            var (exitCode, _, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            if (exitCode == 0)
            {
                return true;
            }

            // Whole-solution restore failed. Don't defer to the target-only build restore (that leaves
            // non-ProjectReference managed siblings unrestored — the NETSDK1004 case this prevents); fall
            // back to per-sibling restore before returning false.
            logger.LogDebug("{UISymbol} Solution restore exited {ExitCode}; falling back to per-project sibling restore.", UiSymbols.Note, exitCode);
        }

        // Native project present (dotnet restore <sln> errors VS-less) or the solution restore failed:
        // restore managed siblings individually and skip natives; the target restores in the normal pass.
        await RestoreSiblingsIndividuallyAsync(siblings, options, workingDir, cancellationToken);
        return false;
    }

    /// <summary>
    /// Best-effort restores each managed sibling project individually (skipping the target, which the normal
    /// build pass restores). Used both when a native sibling forces a per-project plan and as the fallback
    /// when a whole-solution restore fails. Each restore is non-fatal; a real error surfaces at build time.
    /// </summary>
    private async Task RestoreSiblingsIndividuallyAsync(
        IReadOnlyList<FileInfo> siblings,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        foreach (var sibling in siblings)
        {
            var args = BuildRestorePassArguments(sibling, options);
            logger.LogDebug("{UISymbol} Restoring solution sibling before build (build-dependency parity): dotnet {Arguments}", UiSymbols.Note, args);
            var (exitCode, _, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            if (exitCode != 0)
            {
                logger.LogDebug("{UISymbol} Sibling restore of {Sibling} exited {ExitCode}; continuing.", UiSymbols.Note, sibling.Name, exitCode);
            }
        }
    }

    /// <summary>
    /// Runs the project-mode build pass, streaming dotnet's output live. Output routing:
    /// <list type="bullet">
    ///   <item><c>--json</c>/<c>--quiet</c>: stream all build output to <b>stderr</b> so stdout stays pure
    ///   JSON / clean. Keeps <c>-tl:off</c>.</item>
    ///   <item>Real interactive terminal: print a <c>🔧 Building…</c> header + dim invocation, then hand the
    ///   terminal to dotnet with <b>inherited stdio</b> so its native terminal logger renders the live build.
    ///   Omits <c>-tl:off</c>.</item>
    ///   <item>Otherwise (agent/CI/redirected): header + dim invocation, then stream output live to stdout.
    ///   Keeps <c>-tl:off</c>.</item>
    /// </list>
    /// Output always streams (never hidden behind a spinner) so success-path warnings stay visible and the
    /// exact injected-arg invocation is self-describing.
    /// </summary>
    internal async Task<int> RunBuildPassAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);

        var banner = $"Building {csproj.Name} ({options.Configuration} | {options.Architecture})...";
        var stopwatch = Stopwatch.StartNew();

        // --json/--quiet: stdout stays pure JSON, so route the invocation AND build output to stderr
        // (Console.Error is synchronized, so concurrent stdout/stderr callbacks are safe).
        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            var redirectedArgs = BuildBuildPassArguments(csproj, options, verbosity, csWinRTMetadataFolder);
            Console.Error.WriteLine($"dotnet {redirectedArgs}");
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, redirectedArgs,
                onOutputLine: static line => Console.Error.WriteLine(line),
                onErrorLine: static line => Console.Error.WriteLine(line),
                cancellationToken);
        }

        // Info-enabled paths (default interactive / --verbose / agent-CI): print the header and the exact
        // dotnet invocation (winapp injects args the user never typed — RID, shim, -p forwarding) so
        // failures are self-describing.
        var nativeTerminal = NativeTerminalGateOverrideForTests?.Invoke()
            ?? ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);
        var buildArgs = BuildBuildPassArguments(csproj, options, verbosity, csWinRTMetadataFolder, nativeTerminal);
        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} {banner}");
        ansiConsole.MarkupLineInterpolated($"[dim]   dotnet {Markup.Escape(buildArgs)}[/]");

        int streamedExit;
        if (nativeTerminal)
        {
            // Real interactive terminal: hand the console to dotnet (inherited stdio, no -tl:off) so its
            // native terminal logger renders the live build directly — single warnings, live progress.
            // winapp never sees the lines; the persistent header/invocation above and ✓ Built below frame it.
            streamedExit = await dotNetService.RunDotnetInheritedAsync(workingDir, buildArgs, cancellationToken);
        }
        else
        {
            // Info-enabled but NOT a TTY (agent/CI/redirected/piped --verbose): stream dotnet's output live
            // to stdout, serializing writes so concurrent stdout/stderr callbacks don't interleave.
            var writeLock = new object();
            void WriteLive(string line)
            {
                lock (writeLock)
                {
                    ansiConsole.WriteLine(line);
                }
            }

            streamedExit = await dotNetService.RunDotnetStreamingAsync(
                workingDir, buildArgs, WriteLive, WriteLive, cancellationToken);
        }

        if (streamedExit == 0)
        {
            PrintBuildSucceeded(csproj, options, stopwatch.Elapsed);
        }

        return streamedExit;
    }

    /// <summary>
    /// Prints the persistent build-completion line (UX). Callers gate this to info-enabled, non-json paths
    /// so it never pollutes <c>--json</c> stdout or a <c>--quiet</c> run.
    /// </summary>
    private void PrintBuildSucceeded(FileInfo csproj, ProjectRunOptions options, TimeSpan elapsed) =>
        ansiConsole.MarkupLineInterpolated(
            $"{UiSymbols.Check} Built {Path.GetFileNameWithoutExtension(csproj.Name)} in {elapsed.TotalSeconds:0.0}s");

    /// <summary>
    /// Maps the CLI's effective log level to a dotnet <c>-v</c> verbosity for the build pass. <c>--verbose</c>
    /// stays at <c>minimal</c> on purpose (it already streams the build live and unlocks winapp's decision
    /// traces; <c>-v normal</c> would bury those under MSBuild task lines). Only <c>--trace</c> cranks dotnet
    /// to <c>normal</c>; <c>--quiet</c> keeps it quiet; everything else is minimal.
    /// </summary>
    private static string ResolveBuildVerbosity(ILogger logger, bool json)
    {
        // --trace: crank dotnet up so the deeper MSBuild log is available when diagnosing a build.
        if (logger.IsEnabled(LogLevel.Trace))
        {
            return "normal";
        }

        // --quiet suppresses Information (and is never combined with --json); keep dotnet quiet too.
        if (!json && !logger.IsEnabled(LogLevel.Information))
        {
            return "quiet";
        }

        // Default AND --verbose (Debug): keep dotnet skimmable. --verbose still streams the build live and
        // shows winapp's LogDebug traces — without the -v normal flood.
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
            else if (name.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase))
            {
                // A bare -p:TargetFramework (no --framework) is PROMOTED to the effective framework and
                // honored, so it's not overridden. It's only overridden when a dedicated --framework
                // resolved a DIFFERENT TFM — warn just then.
                var value = property.Split('=', 2).ElementAtOrDefault(1)?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(options.Framework) &&
                    !options.Framework.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug(
                        "{UISymbol} -p:{Property} is overridden by --framework '{Framework}' (matches dotnet precedence).",
                        UiSymbols.Note, property, options.Framework);
                }
            }
            else if (name.Equals("Platform", StringComparison.OrdinalIgnoreCase))
            {
                // Project mode conveys arch via the RuntimeIdentifier only and does NOT inject a global
                // Platform, so a user -p:Platform is forwarded as-is. The RID still follows --arch, so an
                // inconsistent pair (e.g. --arch x86 -p:Platform=ARM64) builds a mismatched app — warn so the
                // divergence isn't silent. (Forcing -p:Platform on a multi-project WinUI app can reintroduce
                // the MSB3030/PRI252 split with no-<Platforms> library references.)
                logger.LogDebug(
                    "{UISymbol} -p:{Property} is forwarded as-is; the RuntimeIdentifier still follows --arch, so ensure they are consistent.",
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

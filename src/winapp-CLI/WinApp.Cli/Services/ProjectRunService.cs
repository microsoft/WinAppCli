// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

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
        "OutputType",
    ];

    /// <summary>Upper bound on build-output lines retained for the spinner failure dump (bounded tail).</summary>
    private const int MaxBuildTailLines = 500;

    /// <summary>
    /// Property names owned by a dedicated <c>-c</c>/<c>-r</c>/<c>-f</c> switch. A same-named user
    /// <c>-p</c> is dropped from BOTH the build and evaluate passes so they can never resolve a different
    /// Configuration/RID/TFM from each other (otherwise <c>-c Debug -p Configuration=Release</c> would
    /// build one output and evaluate/launch another). Matches the documented "dedicated flag wins"
    /// contract (see <see cref="WarnOnOverriddenFlags"/>). <c>Platform</c> is intentionally NOT reserved:
    /// project mode forwards it as-is and conveys arch via the RuntimeIdentifier only.
    /// </summary>
    private static readonly string[] DedicatedFlagProperties = ["Configuration", "RuntimeIdentifier", "TargetFramework"];

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
    private string? ResolveCsWinRTMetadataShim(ProjectRunOptions options)
    {
        if (UserSetCsWinRTMetadata(options))
        {
            return null;
        }

        return csWinRTMetadataShimService.ResolveMetadataFolder(options.Framework);
    }

    /// <summary>
    /// True when the user supplied their own <c>-p:CsWinRTWindowsMetadata=…</c>; their value wins and the
    /// shim must not inject (or trigger a restore to resolve) anything.
    /// </summary>
    private static bool UserSetCsWinRTMetadata(ProjectRunOptions options) =>
        options.Properties.Any(p =>
            p.StartsWith("CsWinRTWindowsMetadata=", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

        // Pin an effective single target framework for a multi-targeted project when the user gave no
        // --framework, BEFORE any pass, so the build, the evaluate pass, packaging and runtime
        // provisioning all agree on one TFM. Without this a plural-<TargetFrameworks> project builds but
        // its evaluate pass hits the cross-targeting outer build (empty TargetDir) → we throw AFTER a
        // successful build (H1). Spec default = first declared TFM. No-op when single-targeted / --framework set.
        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);

        // SHIM (temporary): on hosts with no registered Windows SDK, resolve a folder of ref-pack winmds
        // to inject as -p:CsWinRTWindowsMetadata so C#/WinRT authoring projects build (see
        // CsWinRTMetadataShimService). Skipped when the user already set the property. null = no injection.
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options);
        var buildOptions = options;

        // Restore ordering: when the target lives in a solution, restore the whole solution's managed
        // projects up front so build-dependency siblings that are NOT ProjectReferences of the target
        // (e.g. an out-of-process COM server built by a custom MSBuild target) have a project.assets.json
        // and the build doesn't fail with NETSDK1004 — matching what VS / `dotnet build <sln>` do. The
        // SHIM restore below then covers the ref pack on genuinely clean SDK-less hosts. Both are gated on
        // actually building AND the user not opting out of restore.
        if (!options.NoBuild && !options.NoRestore)
        {
            // (1) Restore the owning solution's managed siblings. When it restores the whole solution
            // (all-managed) it also restored the target, so the passes below can skip their own restore.
            var restoredWholeSolution = await RestoreSolutionSiblingsAsync(csproj, options, workingDir, cancellationToken);

            // (2) SHIM (temporary) — ref-pack ordering: on a genuinely clean SDK-less host the ref pack
            // (Microsoft.Windows.SDK.NET.Ref) may not be on disk yet when we first resolve the shim, so the
            // shim no-ops and the very first `dotnet build` — handed no CsWinRTWindowsMetadata — fails even
            // though that same build restores the ref pack; only a SECOND invocation (cache warm) succeeds.
            // Pre-populate the ref pack with an explicit restore, then re-resolve so the first build gets the
            // winmd folder. Only fires when the shim would otherwise inject (no SDK registered) and the user
            // didn't set the property himself.
            if (csWinRTMetadata is null
                && !UserSetCsWinRTMetadata(options)
                && csWinRTMetadataShimService.IsWindowsSdkAbsent())
            {
                var restoreExit = restoredWholeSolution
                    ? 0
                    : await RunRestorePassAsync(csproj, options, workingDir, cancellationToken);
                if (restoreExit == 0)
                {
                    csWinRTMetadata = ResolveCsWinRTMetadataShim(options);
                    // The explicit restore already populated the cache; skip the redundant restore in the
                    // build pass so we don't restore twice.
                    buildOptions = options with { NoRestore = true };
                }
            }
            else if (restoredWholeSolution)
            {
                // The whole-solution restore already covered the target; skip the build pass's own restore.
                buildOptions = options with { NoRestore = true };
            }
        }

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
            var buildExit = await RunBuildPassAsync(csproj, buildOptions, workingDir, useLiveSpinner, csWinRTMetadata, cancellationToken);
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
            options.Architecture,
            options.Framework);

        return new ProjectBuildOutcome(resolution, 0);
    }

    /// <inheritdoc />
    public async Task<bool> IsDefinitivelyUnpackagedAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        // Pin the same effective TFM the real build/evaluate passes use (H1) so a multi-targeted project
        // is probed against a single-TFM inner build, not the empty cross-targeting outer node.
        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options);

        // Reuse the exact evaluate pass (same -p/RID/Platform/TFM/shim as a real build) so the
        // WindowsPackageType we read matches what the build would see. It is evaluate-only — no build
        // is triggered — which is why this is cheap enough to run before deciding to build.
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
            // Starting or communicating with dotnet failed (e.g. a transient Win32Exception) →
            // indeterminate. Don't crash the run before the authoritative build; let the normal build +
            // gate surface the real error and classify packaging.
            return false;
        }

        if (exitCode != 0)
        {
            // Evaluation failed → indeterminate. Don't fail fast; let the normal build + authoritative
            // gate surface the real error and classify packaging.
            return false;
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);

        // Only an EXPLICIT WindowsPackageType=None is treated as definitive. An unset value is NOT —
        // a packaged app that declares identity via an emitted recipe (rather than the property) also
        // evaluates empty here pre-build, so DeterminePackaging's post-build recipe fallback must stay
        // authoritative. Reporting "unpackaged" on empty would misclassify that app and wrongly reject
        // its packaged-only options.
        return string.Equals(GetProp(props, "WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase);
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
    /// SHIM (temporary) — restore ordering: runs an explicit <c>dotnet restore</c> so the
    /// <c>Microsoft.Windows.SDK.NET.Ref</c> ref pack lands on disk BEFORE the shim resolves its winmd
    /// folder, fixing the clean-host first-build failure (see <c>BuildAndResolveAsync</c>). Output is
    /// captured (not streamed) since it's a fast pre-step; the following build pass streams as usual.
    /// Returns the dotnet exit code so the caller only skips the build's own restore when this succeeded.
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
    /// siblings that are not <c>ProjectReference</c>s of the target still have a <c>project.assets.json</c>
    /// (NETSDK1004 parity with VS / <c>dotnet build &lt;sln&gt;</c>). Only fires for a solution-resolved run
    /// (<see cref="ProjectRunOptions.Solution"/> non-null). When every listed project is managed, a single
    /// <c>dotnet restore &lt;sln&gt;</c> restores the whole graph (including the target) and this returns
    /// <see langword="true"/> so the caller can skip the build pass's own restore. When a native project
    /// (<c>.vcxproj</c>/<c>.wapproj</c>/<c>.shproj</c>) is present — which <c>dotnet restore</c> can't handle
    /// on a VS-less box — OR the whole-solution restore fails, the managed siblings are restored individually
    /// (the target is left to the normal restore) and this returns <see langword="false"/>. All restores are
    /// non-fatal (best-effort); the build pass surfaces any real error.
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

            // Whole-solution restore failed (e.g. a transient error). Don't just defer to the target-only
            // build restore — that would leave non-ProjectReference managed siblings unrestored, the exact
            // NETSDK1004 case this pre-step exists to prevent. Fall back to restoring the siblings
            // individually before returning false so the target restore is left to the normal build pass.
            logger.LogDebug("{UISymbol} Solution restore exited {ExitCode}; falling back to per-project sibling restore.", UiSymbols.Note, exitCode);
        }

        // Either a native project is present (so `dotnet restore <sln>` would error on a VS-less host) or the
        // whole-solution restore failed. Restore the managed siblings individually and skip the natives; the
        // target is restored by the normal pass.
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
    /// Builds the argument string for the SHIM's pre-build <c>dotnet restore</c>. It mirrors the build
    /// pass's effective values — the RID (<c>-r win-&lt;arch&gt;</c>), the configuration (as
    /// <c>-p:Configuration=</c>, since <c>dotnet restore</c> has no <c>-c</c> switch), user <c>-p</c>, and
    /// solution properties — so the same graph resolves into <c>project.assets.json</c> that the
    /// subsequent <c>--no-restore</c> build consumes. Dedicated-flag user <c>-p</c>
    /// (Configuration/RuntimeIdentifier/TargetFramework) are filtered out via
    /// <see cref="ForwardableProperties"/> so a conflicting <c>-p:RuntimeIdentifier</c> can't (MSBuild
    /// last-wins) restore a different RID's assets than the build needs. Verbosity (<c>-v</c>) is omitted
    /// (restore output is captured, not streamed) and it never adds <c>--no-restore</c> (restoring is the
    /// whole point). Pure and unit-testable.
    /// </summary>
    internal static string BuildRestorePassArguments(FileInfo csproj, ProjectRunOptions options)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);
        var tokens = new List<string>
        {
            "restore",
            csproj.FullName,
            "-r",
            rid,
            // 'dotnet restore' has no -c switch, so the configuration flows as an MSBuild property. This
            // makes config-conditional <PackageReference Condition="'$(Configuration)'=='Release'"> land
            // in project.assets.json BEFORE the --no-restore build consumes them.
            $"-p:Configuration={options.Configuration}",
        };

        // Forward user -p EXCEPT the dedicated-flag properties the -r / -p:Configuration above own — a
        // conflicting user -p:RuntimeIdentifier/TargetFramework/Configuration would otherwise diverge the
        // restored graph from what the --no-restore build resolves. WarnOnOverriddenFlags surfaces it.
        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the argument string for the streaming BUILD pass: a plain <c>dotnet build</c> that
    /// produces the output and STREAMS its console log. It deliberately omits <c>--getProperty</c>
    /// (which suppresses that log) and needs no explicit <c>-t:Build</c> (Build is the default
    /// target). The dedicated <c>-c</c>/<c>-r</c>/<c>-f</c> switches always beat a same-named user
    /// <c>-p</c>. Architecture is conveyed by the RID (<c>-r win-&lt;arch&gt;</c>) ONLY — project mode
    /// does NOT force a global <c>-p:Platform</c> (nor its <c>EnableDynamicPlatformResolution</c>
    /// companion), matching how Visual Studio and a plain <c>dotnet build -r win-&lt;arch&gt;</c> convey
    /// arch. A forced global Platform de-synchronizes a no-<c>&lt;Platforms&gt;</c> WinUI library
    /// reference (its XAML/MRT outputs compile to the AnyCPU <c>bin\Debug\…</c> path while the app's
    /// Platform-driven lookup expects <c>bin\&lt;arch&gt;\Debug\…</c>) → MSB3030/PRI252. The RID alone
    /// still yields the correct packaged manifest <c>ProcessorArchitecture</c> and apphost arch. A user
    /// who explicitly passes <c>-p:Platform=…</c>/<c>-p:EnableDynamicPlatformResolution=…</c> still has
    /// it forwarded (via the user <c>-p</c> loop). The <c>-v</c> verbosity is mapped from the CLI's log
    /// level (Change #1, spec §8.3/§8.5).
    /// </summary>
    internal static string BuildBuildPassArguments(FileInfo csproj, ProjectRunOptions options, string verbosity, string? csWinRTMetadataFolder = null)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);

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

        // Forward user -p properties, but drop any that duplicate a dedicated -c/-r/-f switch so this
        // build pass and the evaluate pass can never resolve a different Configuration/RID/TFM (see
        // DedicatedFlagProperties / WarnOnOverriddenFlags). A user-supplied -p:Platform / EDPR still
        // flows through and is respected — project mode itself never injects them.
        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        // When the target was resolved from a solution, define $(SolutionDir) and its siblings so
        // projects that reference them build exactly as they do under `dotnet build <sln>` / VS.
        AppendSolutionProperties(tokens, options);

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

        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };

        // Forward user -p, dropping any that duplicate a dedicated switch (same filter as the build pass)
        // so the two passes stay in lock-step on Configuration/RID/TFM; the dedicated -p: equivalents are
        // then emitted below. A user-supplied -p:Platform / EDPR flows through here and is respected;
        // project mode never injects them (arch is conveyed by RuntimeIdentifier only).
        foreach (var property in ForwardableProperties(options.Properties))
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

        // User -p properties are emitted before this call, and MSBuild is last-wins, so re-emitting a
        // Solution* property the user already set would clobber their value. Skip any the user specified
        // so an explicit `-p:SolutionDir=…` (or sibling) always wins. Covers every solution-attached
        // target — bare-.csproj-with-owning-sln, directory-resolved, and explicit-solution alike.
        foreach (var token in BuildSolutionPropertyTokens(solution))
        {
            if (UserSpecifiesProperty(options.Properties, SolutionPropertyName(token)))
            {
                continue;
            }

            tokens.Add(token);
        }
    }

    /// <summary>
    /// Builds the MSBuild <c>-p:</c> property tokens used to classify runnable candidates so the
    /// evaluate reads <c>OutputType</c>/test markers under the SAME globals the build will use. Mirrors
    /// the property section of <see cref="BuildEvaluateArguments"/>: forwardable user <c>-p</c> first,
    /// then the <c>Solution*</c> props (solution targets only, skipping any the user set so their value
    /// wins), then Configuration/RID/TargetFramework LAST so MSBuild's last-wins makes a dedicated value
    /// beat a conflicting user <c>-p</c>. When <paramref name="inputs"/> is null, preserves the prior
    /// behavior: solution props only (or nothing for a directory), letting classification use MSBuild's
    /// defaults for Configuration/Platform/RID/TFM.
    /// </summary>
    private static IReadOnlyList<string> BuildClassificationPropertyTokens(
        ProjectClassificationInputs? inputs,
        FileInfo? solution)
    {
        if (inputs is null)
        {
            return solution is null ? [] : BuildSolutionPropertyTokens(solution);
        }

        var tokens = new List<string>();

        foreach (var property in ForwardableProperties(inputs.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        if (solution is not null)
        {
            foreach (var token in BuildSolutionPropertyTokens(solution))
            {
                if (UserSpecifiesProperty(inputs.Properties, SolutionPropertyName(token)))
                {
                    continue;
                }

                tokens.Add(token);
            }
        }

        tokens.Add($"-p:Configuration={inputs.Configuration}");
        tokens.Add($"-p:RuntimeIdentifier={RunArchHelper.ToRuntimeIdentifier(inputs.Architecture)}");
        if (!string.IsNullOrWhiteSpace(inputs.Framework))
        {
            tokens.Add($"-p:TargetFramework={inputs.Framework}");
        }

        return tokens;
    }

    /// <summary>True when the user passed a <c>-p Name=Value</c> for <paramref name="name"/> (case-insensitive).</summary>
    private static bool UserSpecifiesProperty(IReadOnlyList<string> properties, string name) =>
        properties.Any(p => p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// User <c>Name=Value</c> properties with any that duplicate a dedicated <c>-c</c>/<c>-r</c>/<c>-f</c>
    /// switch removed (see <see cref="DedicatedFlagProperties"/>). Applied to BOTH the build and evaluate
    /// passes so the dedicated switch is the single source of Configuration/RID/TFM in each — a conflicting
    /// user <c>-p</c> is already surfaced by <see cref="WarnOnOverriddenFlags"/>.
    /// </summary>
    private static IEnumerable<string> ForwardableProperties(IReadOnlyList<string> properties) =>
        properties.Where(p => !IsDedicatedFlagProperty(p));

    /// <summary>
    /// True when a <c>Name=Value</c> property names a dedicated-switch property (case-insensitive).
    /// Defense-in-depth: even though project-mode validation rejects a ';'-packed <c>-p</c> up front (a
    /// single MSBuild <c>/p</c> token splits on ';' into multiple properties), split on ';' here too and
    /// treat the token as dedicated if ANY packed segment is a dedicated-flag property — so a smuggled
    /// <c>RuntimeIdentifier</c>/<c>Configuration</c>/<c>TargetFramework</c> can never slip through
    /// forwarding and override the switch winapp sets.
    /// </summary>
    private static bool IsDedicatedFlagProperty(string property) =>
        property.Split(';')
            .Select(segment => segment.Split('=', 2)[0].Trim())
            .Any(name => DedicatedFlagProperties.Any(d => name.Equals(d, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Extracts the property name from a <c>-p:Name=Value</c> token (e.g. <c>SolutionDir</c>).</summary>
    private static string SolutionPropertyName(string token)
    {
        var start = token.StartsWith("-p:", StringComparison.Ordinal) ? 3 : 0;
        var equals = token.IndexOf('=', start);
        return equals > start ? token[start..equals] : token[start..];
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

        // MSBuild-escape each value: an unescaped ';' in a legal NTFS path would be read as a property
        // separator (silently truncating $(SolutionDir) and injecting a bogus extra property), and a
        // literal '%' could be mis-decoded as an escape. Command-line quoting (EscapeArgument, applied
        // when these tokens become process args) is a *separate* layer and does not cover MSBuild's own
        // value grammar.
        return
        [
            $"-p:SolutionDir={EscapeMsBuildPropertyValue(solutionDir)}",
            $"-p:SolutionPath={EscapeMsBuildPropertyValue(solution.FullName)}",
            $"-p:SolutionName={EscapeMsBuildPropertyValue(solutionName)}",
            $"-p:SolutionFileName={EscapeMsBuildPropertyValue(solution.Name)}",
            $"-p:SolutionExt={EscapeMsBuildPropertyValue(solution.Extension)}",
        ];
    }

    /// <summary>
    /// Percent-escapes the characters MSBuild treats specially inside a property value passed via
    /// <c>-p:Name=Value</c> — most importantly <c>;</c> (the property/item separator) and <c>%</c> (the
    /// escape lead-in). Escaping <c>%</c> first keeps the transform idempotent-safe. Other special
    /// characters (<c>$ @ ' " ( )</c>) are inert in a command-line property value and left as-is so paths
    /// stay readable in logs.
    /// </summary>
    private static string EscapeMsBuildPropertyValue(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
             .Replace(";", "%3B", StringComparison.Ordinal);
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
                // Project mode conveys arch via the RuntimeIdentifier only and does NOT inject a global
                // Platform, so a user -p:Platform is forwarded as-is. The RID still follows --arch, so an
                // inconsistent pair (e.g. --arch x86 -p:Platform=ARM64) builds a mismatched app — warn so
                // the divergence isn't silent. Note: forcing -p:Platform on a multi-project WinUI app can
                // reintroduce the MSB3030/PRI252 split with no-<Platforms> library references.
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

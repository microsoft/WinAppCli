// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// WinUI analyzer detection and injection for <see cref="ProjectRunService"/>'s build pass (issue #634).
///
/// Flow: a cheap text scan gates an out-of-band MSBuild probe; the probe confirms the project is WinUI,
/// reads any pre-existing <c>CustomAfterMicrosoftCommonTargets</c> (so the injection can re-chain it),
/// and detects an existing analyzer <c>PackageReference</c> (detect-and-skip, design D8). When injection
/// is warranted the embedded analyzer + hook props are materialized and their paths threaded onto the
/// build pass only.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// The analyzer package a user might already reference; when present we skip injecting so the build
    /// never sees the analyzer twice. Single swappable detect-and-skip predicate input (design D8).
    /// </summary>
    internal const string AnalyzerPackageId = "Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer";

    /// <summary>
    /// The resolved decision to inject the analyzer into a build pass: the hook props to thread via
    /// <c>-p:CustomAfterMicrosoftCommonTargets=</c> and the user's original value to re-chain via
    /// <c>-p:_WinAppChainedCustomAfter=</c> (empty when the user had none).
    /// </summary>
    internal sealed record AnalyzerBuildInjection(string HookPropsPath, string ChainedCustomAfter);

    /// <summary>
    /// The out-of-band probe result backing the injection decision.
    /// </summary>
    private sealed record AnalyzerProbe(bool UseWinUI, bool AlreadyReferencesAnalyzer, string ExistingCustomAfter);

    /// <summary>
    /// Decides whether to inject the WinUI analyzer for this build and, if so, materializes the assets and
    /// returns the paths to thread onto the build pass. Returns <see langword="null"/> to inject nothing —
    /// the build proceeds exactly as before. Never throws: any failure degrades to "no injection".
    /// </summary>
    internal async Task<AnalyzerBuildInjection?> TryPrepareAnalyzerInjectionAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        try
        {
            // Cheap gate: avoid an MSBuild evaluation for the common non-WinUI project. The probe below
            // is authoritative; this only decides whether the probe is worth running.
            if (!LooksLikeWinUi(csproj))
            {
                return null;
            }

            AnalyzerProbe? probe = await ProbeAnalyzerContextAsync(csproj, options, workingDir, cancellationToken);
            if (probe is null || !probe.UseWinUI)
            {
                return null;
            }

            if (probe.AlreadyReferencesAnalyzer)
            {
                logger.LogDebug(
                    "{UISymbol} '{Project}' already references {Package}; skipping analyzer injection.",
                    UiSymbols.Note, csproj.Name, AnalyzerPackageId);
                return null;
            }

            AnalyzerInjection? injection = analyzerInjectionService.PrepareInjection();
            if (injection is null)
            {
                return null;
            }

            return new AnalyzerBuildInjection(injection.HookPropsPath, probe.ExistingCustomAfter);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The analyzer is a best-effort quality gate; never let it break a build.
            logger.LogDebug(ex, "{UISymbol} WinUI analyzer injection preparation failed; continuing without it.", UiSymbols.Note);
            return null;
        }
    }

    /// <summary>
    /// Cheap text heuristic: does this project (or a Directory.Build.props/.targets above it) mention
    /// WinUI at all? Matches <c>UseWinUI</c> or the <c>Microsoft.WindowsAppSDK</c> package, which every
    /// real WinUI 3 project carries. A false positive costs one wasted probe; the probe then rejects it.
    /// </summary>
    internal static bool LooksLikeWinUi(FileInfo csproj)
    {
        if (FileMentionsWinUi(csproj))
        {
            return true;
        }

        // UseWinUI / the WindowsAppSDK reference are frequently centralized in a Directory.Build.props
        // (or .targets) above the project. Walk up to (and including) the drive root.
        DirectoryInfo? dir = csproj.Directory;
        while (dir is not null)
        {
            foreach (string name in DirectoryBuildFileNames)
            {
                var candidate = new FileInfo(Path.Combine(dir.FullName, name));
                if (candidate.Exists && FileMentionsWinUi(candidate))
                {
                    return true;
                }
            }

            dir = dir.Parent;
        }

        return false;
    }

    private static readonly string[] DirectoryBuildFileNames =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    private static bool FileMentionsWinUi(FileInfo file)
    {
        try
        {
            string text = File.ReadAllText(file.FullName);
            return text.Contains("UseWinUI", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Out-of-band evaluation (no build, no restore) that reads the effective <c>UseWinUI</c>, the user's
    /// existing <c>CustomAfterMicrosoftCommonTargets</c> (so the injection can re-chain it rather than
    /// shadow it), and the resolved <c>PackageReference</c> items (to honor an existing analyzer reference).
    /// Mirrors the build pass's Configuration / Platform / solution / user <c>-p</c> so conditional values
    /// evaluate the same way they will at build time.
    /// </summary>
    private async Task<AnalyzerProbe?> ProbeAnalyzerContextAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        string arguments = BuildAnalyzerProbeArguments(csproj, options);
        var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, arguments, cancellationToken);
        if (exitCode != 0)
        {
            return null;
        }

        var props = MsBuildPropertyReader.Parse(stdout, ["UseWinUI", "CustomAfterMicrosoftCommonTargets"]);
        bool useWinUI = props.TryGetValue("UseWinUI", out var uw)
            && string.Equals(uw.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        string existingCustomAfter = props.TryGetValue("CustomAfterMicrosoftCommonTargets", out var ca)
            ? ca.Trim()
            : string.Empty;

        var items = MsBuildPropertyReader.ParseItems(stdout);
        bool alreadyReferences = ReferencesAnalyzerPackage(items);

        return new AnalyzerProbe(useWinUI, alreadyReferences, existingCustomAfter);
    }

    /// <summary>
    /// Detect-and-skip predicate (design D8). True when the project's evaluated <c>@(PackageReference)</c>
    /// already includes the analyzer package (by <c>Identity</c>, which is present under both Central
    /// Package Management and inline versions — see spike A2). Keeping this a single method makes it easy
    /// to widen later (e.g. to enumerate resolved <c>@(Analyzer)</c> items for transitive delivery).
    /// </summary>
    private static bool ReferencesAnalyzerPackage(IReadOnlyDictionary<string, IReadOnlyList<string>> items) =>
        items.TryGetValue("PackageReference", out var refs)
        && refs.Any(id => string.Equals(id, AnalyzerPackageId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the evaluate-only probe arguments: <c>dotnet msbuild &lt;csproj&gt; --getProperty:UseWinUI
    /// --getProperty:CustomAfterMicrosoftCommonTargets --getItem:PackageReference</c> plus the SAME
    /// effective MSBuild globals the build pass uses (Configuration / RuntimeIdentifier /
    /// TargetFramework / Platform / solution / forwardable user <c>-p</c>), so a project whose
    /// <c>CustomAfterMicrosoftCommonTargets</c> or analyzer <c>PackageReference</c> is conditional on the
    /// RID or TFM evaluates the same value the build will see (else a conditional user hook is read as
    /// empty here and then silently shadowed by the injected global). It deliberately does NOT set
    /// <c>CustomAfterMicrosoftCommonTargets</c> (that is what we are reading).
    /// </summary>
    internal static string BuildAnalyzerProbeArguments(FileInfo csproj, ProjectRunOptions options)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);

        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
            "--getProperty:UseWinUI",
            "--getProperty:CustomAfterMicrosoftCommonTargets",
            "--getItem:PackageReference",
            $"-p:Configuration={options.Configuration}",
        };

        // Mirror the build/evaluate passes' effective RID and TFM so a hook / PackageReference
        // conditioned on either resolves identically to what the build sees.
        if (!options.OmitRuntimeIdentifier)
        {
            tokens.Add($"-p:RuntimeIdentifier={rid}");
        }

        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add($"-p:TargetFramework={options.Framework}");
        }

        if (!string.IsNullOrWhiteSpace(options.Platform))
        {
            tokens.Add($"-p:Platform={options.Platform}");
        }

        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Effective-target-framework resolution for a multi-targeted project: pins a single TFM so every
/// downstream pass (build, evaluate, packaging, runtime provisioning) agrees, preferring a cheap static
/// read and falling back to an authoritative MSBuild evaluate only when the TFM list can't be resolved
/// statically. Split from the main <see cref="ProjectRunService"/> partial to keep each file cohesive.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// Resolves an <em>effective</em> single target framework for a multi-targeted project so every
    /// downstream pass (build, evaluate, packaging, runtime provisioning) pins the SAME TFM. When the user
    /// gave no <c>--framework</c> it pins the FIRST declared TFM — the spec default. It first tries a cheap
    /// static read of the project file's inline <c>&lt;TargetFramework(s)&gt;</c>; when the TFM(s) can't be
    /// resolved statically — an inline <c>&lt;TargetFrameworks&gt;</c> whose first entry is an MSBuild
    /// expression, or a project whose <c>TargetFrameworks</c> come from an import (<c>Directory.Build.props</c>)
    /// or are conditional on Configuration/user <c>-p</c> — it falls back to an authoritative
    /// <c>dotnet msbuild --getProperty:TargetFrameworks</c> evaluate using the SAME globals. Without this a
    /// plural-<c>&lt;TargetFrameworks&gt;</c> project builds but its evaluate pass hits the cross-targeting
    /// outer node (empty <c>TargetDir</c>) → we throw AFTER a successful build (H1). No-op when the user set
    /// <c>--framework</c>, the project is genuinely single-targeted, or a TFM still can't be determined
    /// (e.g. SDK-less / pre-restore) — the normal passes then surface any real error.
    /// </summary>
    private async Task<ProjectRunOptions> ResolveEffectiveFrameworkAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            return options;
        }

        var staticFirst = dotNetService.GetTargetFramework(csproj);
        var staticFirstIsConcrete = !string.IsNullOrWhiteSpace(staticFirst)
            && !staticFirst.Contains("$(", StringComparison.Ordinal);

        if (dotNetService.IsMultiTargeted(csproj))
        {
            // Inline <TargetFrameworks>: pin the first statically when it's a concrete value — the common,
            // cheap case that needs no MSBuild round-trip.
            if (staticFirstIsConcrete)
            {
                LogEffectiveFrameworkPinned(csproj, staticFirst!);
                return options with { Framework = staticFirst };
            }
        }
        else if (staticFirstIsConcrete)
        {
            // Inline single <TargetFramework> with a concrete value: genuinely single-targeted — the build
            // and evaluate passes already resolve the one TFM identically, so no pinning is needed.
            return options;
        }

        // The TFM list isn't statically resolvable (imported / conditional / expression). Authoritatively
        // evaluate the effective TargetFrameworks with MSBuild (same globals) and pin the FIRST.
        var evaluatedFirst = await ResolveFirstMultiTargetFrameworkAsync(csproj, options, workingDir, cancellationToken);
        if (string.IsNullOrWhiteSpace(evaluatedFirst))
        {
            return options;
        }

        LogEffectiveFrameworkPinned(csproj, evaluatedFirst);
        return options with { Framework = evaluatedFirst };
    }

    private void LogEffectiveFrameworkPinned(FileInfo csproj, string tfm) =>
        logger.LogDebug(
            "{UISymbol} '{Project}' is multi-targeted; defaulting to first target framework '{Tfm}'. Pass --framework to choose another.",
            UiSymbols.Note, csproj.Name, tfm);

    /// <summary>MSBuild property queried to authoritatively discover a project's effective TFM list.</summary>
    private static readonly string[] FrameworkDiscoveryProperties = ["TargetFrameworks"];

    /// <summary>
    /// Authoritatively resolves the FIRST entry of a project's effective <c>TargetFrameworks</c> via an
    /// evaluate-only <c>dotnet msbuild --getProperty:TargetFrameworks</c> (no build), using the same
    /// Configuration / forwardable user <c>-p</c> / <c>Solution*</c> globals as the real passes so a
    /// Configuration- or property-conditional list resolves identically. Returns <c>null</c> when the
    /// project is single-targeted (empty <c>TargetFrameworks</c>) or the evaluate can't run (SDK-less /
    /// pre-restore / cancelled-start), leaving the caller to no-op gracefully.
    /// </summary>
    private async Task<string?> ResolveFirstMultiTargetFrameworkAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var args = BuildFrameworkDiscoveryArguments(csproj, options);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, args);

        int exitCode;
        string stdout;
        try
        {
            (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Starting or communicating with dotnet failed → indeterminate; let the normal passes surface
            // any real error rather than crash the run before the authoritative build.
            return null;
        }

        if (exitCode != 0)
        {
            return null;
        }

        var props = MsBuildPropertyReader.Parse(stdout, FrameworkDiscoveryProperties);
        var targetFrameworks = GetProp(props, "TargetFrameworks");
        if (string.IsNullOrWhiteSpace(targetFrameworks))
        {
            // Single-targeted (TargetFrameworks empty, TargetFramework set) — nothing to pin.
            return null;
        }

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    /// <summary>
    /// Builds the evaluate-only <c>dotnet msbuild --getProperty:TargetFrameworks</c> arguments for
    /// discovering a project's effective TFM list. Mirrors the property section of
    /// <see cref="BuildEvaluateArguments"/> — forwardable user <c>-p</c>, then <c>Solution*</c> props, then
    /// <c>-p:Configuration</c> LAST — but deliberately omits <c>-p:TargetFramework</c> (that is what we're
    /// discovering) and the RID (which does not select the TFM list) so the outer cross-targeting node is
    /// evaluated.
    /// </summary>
    internal static string BuildFrameworkDiscoveryArguments(FileInfo csproj, ProjectRunOptions options)
    {
        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };

        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        tokens.Add($"-p:Configuration={options.Configuration}");
        tokens.Add("--getProperty:TargetFrameworks");

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }
}

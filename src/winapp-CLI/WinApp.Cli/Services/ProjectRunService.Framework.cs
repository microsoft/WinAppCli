// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Xml.Linq;
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
    /// gave no <c>--framework</c> it pins the FIRST declared TFM — the default. It first tries a cheap
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

        // No dedicated --framework, but the user forwarded an explicit -p:TargetFramework=X. TFM is a
        // DedicatedFlagProperty, so ForwardableProperties would otherwise strip that -p from every pass
        // and silently build/run the project's default TFM instead — dropping the user's choice. Promote
        // it to the effective framework so it flows through the dedicated -f / -p:TargetFramework path
        // consistently across the build and evaluate passes (and beats the auto-pin of a multi-targeted
        // project's first TFM — an explicit request wins over the default). An explicit --framework still
        // wins over this (handled by the early return above). NOTE: the command layer now resolves this SAME
        // explicit framework up front (ProjectRunService.ResolveExplicitFramework) and threads it into BOTH
        // options.Framework and the classification pass, so in the normal flow the early return above already
        // fired; this block is a service-level fallback that preserves the precedence for direct callers/tests.
        if (TryGetUserProperty(options.Properties, "TargetFramework", out var userFramework))
        {
            return options with { Framework = userFramework };
        }

        var staticFirst = dotNetService.GetTargetFramework(csproj);
        var staticFirstIsConcrete = !string.IsNullOrWhiteSpace(staticFirst)
            && !staticFirst.Contains("$(", StringComparison.Ordinal);

        if (dotNetService.IsMultiTargeted(csproj))
        {
            // Inline <TargetFrameworks>: pin the first statically ONLY when the declaration is a concrete
            // value AND unconditional (a single element with no Condition on it or any ancestor). The static
            // read is a first-textual-match, so a conditional/duplicated declaration — e.g. per-Configuration
            // <TargetFrameworks> groups — could pin the wrong group's TFM (a Release run picking Debug's
            // first). In that case fall through to the authoritative MSBuild evaluate, which honors the same
            // Configuration/property globals.
            if (staticFirstIsConcrete && HasUnconditionalInlineTargetFrameworks(csproj))
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

    /// <summary>
    /// True when the project's multi-targeting is declared inline in a form the cheap static read can trust:
    /// exactly ONE <c>&lt;TargetFrameworks&gt;</c> element, and neither it nor any ancestor (its
    /// <c>&lt;PropertyGroup&gt;</c>, a <c>&lt;Choose&gt;/&lt;When&gt;</c>, the <c>&lt;Project&gt;</c>) carries
    /// a <c>Condition</c>. A conditional or duplicated declaration (e.g. per-Configuration variants) makes the
    /// first-textual-match static read unreliable, so the caller must fall back to an authoritative MSBuild
    /// evaluate that resolves the effective list under the real globals. Returns <see langword="false"/> on
    /// unreadable/invalid XML so the evaluate path decides.
    /// </summary>
    private static bool HasUnconditionalInlineTargetFrameworks(FileInfo csproj)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(csproj.FullName);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // SDK-style project files are namespace-less; match on local name to stay namespace-agnostic.
        var declarations = doc.Descendants()
            .Where(e => e.Name.LocalName == "TargetFrameworks")
            .ToList();

        if (declarations.Count != 1)
        {
            return false;
        }

        return !declarations[0].AncestorsAndSelf()
            .Any(a => !string.IsNullOrWhiteSpace(a.Attribute("Condition")?.Value));
    }

    /// <summary>
    /// MSBuild properties queried to authoritatively discover a project's effective TFM list. Deliberately
    /// requests TWO properties (not just <c>TargetFrameworks</c>) so the SDK emits the JSON envelope
    /// <c>{ "Properties": { ... } }</c> rather than a raw scalar: a single <c>--getProperty</c> makes the
    /// whole trimmed stdout the value, so any evaluation warning or diagnostic preamble would be captured as
    /// <c>TargetFrameworks</c>, split into a garbage first TFM, and passed to <c>-f</c>. Requesting the extra
    /// (empty-on-the-outer-node) <c>TargetFramework</c> is harmless and forces the diagnostic-tolerant JSON
    /// parse in <see cref="MsBuildPropertyReader"/>.
    /// </summary>
    private static readonly string[] FrameworkDiscoveryProperties = ["TargetFramework", "TargetFrameworks"];

    /// <summary>
    /// Authoritatively resolves the FIRST entry of a project's effective <c>TargetFrameworks</c> via an
    /// evaluate-only <c>dotnet msbuild --getProperty:TargetFramework --getProperty:TargetFrameworks</c> (no
    /// build — two properties force the diagnostic-tolerant JSON envelope), using the same
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
    /// Builds the evaluate-only <c>dotnet msbuild --getProperty:...</c> arguments for discovering a
    /// project's framework properties. Mirrors the property section of <see cref="BuildEvaluateArguments"/>
    /// — forwardable user <c>-p</c>, then <c>Solution*</c> props, then <c>-p:Configuration</c> LAST — but
    /// deliberately omits <c>-p:TargetFramework</c> (that is what we're discovering) and the RID (which does
    /// not select the TFM list) so the outer cross-targeting node is evaluated. Defaults to querying both
    /// <c>TargetFramework</c> and <c>TargetFrameworks</c> (two properties force the diagnostic-tolerant JSON
    /// envelope — see <see cref="FrameworkDiscoveryProperties"/>); pass explicit <paramref name="getProperties"/>
    /// to query others.
    /// </summary>
    internal static string BuildFrameworkDiscoveryArguments(FileInfo csproj, ProjectRunOptions options, params string[] getProperties)
    {
        var properties = getProperties.Length > 0 ? getProperties : FrameworkDiscoveryProperties;

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
        foreach (var property in properties)
        {
            tokens.Add($"--getProperty:{property}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Resolves the single effective target framework moniker used ONLY to steer the CsWinRT metadata shim's
    /// ref-pack selection toward the project's <c>TargetPlatformVersion</c> (e.g. <c>10.0.19041</c>). Unlike
    /// <see cref="ResolveEffectiveFrameworkAsync"/> — which pins a build TFM only for a MULTI-targeted
    /// project — this also covers a genuinely single-targeted project (whose <c>--framework</c> stays null),
    /// so on an SDK-less host the shim prefers the matching ref pack instead of the highest cached one.
    /// Order: an already pinned / user <c>--framework</c>, then a cheap static <c>&lt;TargetFramework&gt;</c>
    /// read, then — only when the shim would actually inject (no registered SDK) and the value isn't
    /// statically resolvable — an authoritative MSBuild evaluate honoring imported/conditional values. The
    /// returned TFM is NEVER used as a build <c>-p:TargetFramework</c> (a single-targeted project needs none).
    /// </summary>
    private async Task<string?> ResolveShimFrameworkAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            return options.Framework;
        }

        var staticTfm = dotNetService.GetTargetFramework(csproj);
        if (!string.IsNullOrWhiteSpace(staticTfm) && !staticTfm.Contains("$(", StringComparison.Ordinal))
        {
            return staticTfm;
        }

        // Only the SDK-less path consumes this framework (the shim no-ops when a Windows SDK is registered),
        // so skip the extra MSBuild evaluate on SDK-installed hosts where the result would be discarded.
        if (!csWinRTMetadataShimService.IsWindowsSdkAbsent())
        {
            return null;
        }

        return await ResolveEvaluatedTargetFrameworkAsync(csproj, options, workingDir, cancellationToken);
    }

    /// <summary>
    /// Authoritatively resolves a project's effective SINGLE target framework moniker via an evaluate-only
    /// <c>dotnet msbuild --getProperty:TargetFramework --getProperty:TargetFrameworks</c> (no build), using
    /// the same globals as the real passes so an imported or conditional value resolves identically. Returns
    /// the singular <c>TargetFramework</c> when set, else the first of <c>TargetFrameworks</c>, else
    /// <c>null</c> when the evaluate can't run (SDK-less / pre-restore / cancelled-start).
    /// </summary>
    private async Task<string?> ResolveEvaluatedTargetFrameworkAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var args = BuildFrameworkDiscoveryArguments(csproj, options, FrameworkDiscoveryProperties);
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
            return null;
        }

        if (exitCode != 0)
        {
            return null;
        }

        var props = MsBuildPropertyReader.Parse(stdout, FrameworkDiscoveryProperties);
        var single = GetProp(props, "TargetFramework");
        if (!string.IsNullOrWhiteSpace(single))
        {
            return single;
        }

        var multi = GetProp(props, "TargetFrameworks");
        return string.IsNullOrWhiteSpace(multi)
            ? null
            : multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }
}

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
/// statically.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// Resolves an <em>effective</em> single TFM for a multi-targeted project so every downstream pass
    /// pins the SAME one; with no <c>--framework</c> it pins the FIRST declared TFM. Tries a cheap static
    /// read first, falling back to an authoritative <c>dotnet msbuild --getProperty:TargetFrameworks</c>
    /// evaluate when the list is imported/conditional/expression-based (without which a plural-TFMs
    /// project builds but its evaluate hits the empty cross-targeting outer node → throw after a
    /// successful build). No-op when single-targeted, <c>--framework</c> is set, or no TFM is determinable.
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

        // No --framework, but the user forwarded an explicit -p:TargetFramework=X. TFM is a
        // DedicatedFlagProperty, so ForwardableProperties would strip it from every pass and silently build
        // the default; promote it so it flows through the -f path and beats the multi-target auto-pin. The
        // command layer normally resolves this up front (ResolveExplicitFramework) so the early return
        // above already fired; this is a service-level fallback for direct callers/tests.
        if (TryGetUserProperty(options.Properties, "TargetFramework", out var userFramework))
        {
            return options with { Framework = userFramework };
        }

        var staticFirst = dotNetService.GetTargetFramework(csproj);
        var staticFirstIsConcrete = !string.IsNullOrWhiteSpace(staticFirst)
            && !staticFirst.Contains("$(", StringComparison.Ordinal);

        if (dotNetService.IsMultiTargeted(csproj))
        {
            // Inline <TargetFrameworks>: pin the first statically ONLY when concrete AND unconditional. The
            // static read is first-textual-match, so a conditional/per-Configuration declaration could pin
            // the wrong group's TFM; fall through to the authoritative MSBuild evaluate in that case.
            if (staticFirstIsConcrete && HasUnconditionalInlineTargetFrameworks(csproj))
            {
                LogEffectiveFrameworkPinned(csproj, staticFirst!);
                return options with { Framework = staticFirst };
            }
        }
        else if (staticFirstIsConcrete)
        {
            // Inline single concrete <TargetFramework>: genuinely single-targeted — no pinning needed.
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
    /// True when multi-targeting is declared inline in a form the cheap static read can trust: exactly ONE
    /// <c>&lt;TargetFrameworks&gt;</c> element with no <c>Condition</c> on it or any ancestor. A conditional
    /// or duplicated declaration makes the first-textual-match read unreliable, forcing the caller to the
    /// authoritative evaluate. Returns <see langword="false"/> on unreadable/invalid XML.
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
    /// MSBuild properties queried to discover a project's effective TFM list. Requests TWO properties (not
    /// just <c>TargetFrameworks</c>) so the SDK emits the JSON envelope rather than a raw scalar — a single
    /// <c>--getProperty</c> would capture any warning/diagnostic preamble as the value. See
    /// <see cref="MsBuildPropertyReader"/>.
    /// </summary>
    private static readonly string[] FrameworkDiscoveryProperties = ["TargetFramework", "TargetFrameworks"];

    /// <summary>
    /// Authoritatively resolves the FIRST entry of a project's effective <c>TargetFrameworks</c> via an
    /// evaluate-only MSBuild pass using the same globals as the real passes. Returns <c>null</c> when the
    /// project is single-targeted or the evaluate can't run (SDK-less / pre-restore / cancelled-start).
    /// </summary>
    private async Task<string?> ResolveFirstMultiTargetFrameworkAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var args = BuildFrameworkDiscoveryArguments(csproj, options);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));

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
            // Starting/communicating with dotnet failed → indeterminate; let the normal passes surface it.
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
            return null; // Single-targeted — nothing to pin.
        }

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    /// <summary>
    /// Builds the evaluate-only <c>dotnet msbuild --getProperty:...</c> arguments for discovering a
    /// project's framework properties. Mirrors the property section of <see cref="BuildEvaluateArguments"/>
    /// but omits <c>-p:TargetFramework</c> (that's what we're discovering) and the RID: this reads the OUTER
    /// cross-targeting <c>&lt;TargetFrameworks&gt;</c> node, which the RID does not select, and pinning one on
    /// a bare cross-targeting evaluation can trip RID-graph resolution. Defaults to both <c>TargetFramework</c>
    /// and <c>TargetFrameworks</c> (two properties force the JSON envelope — see <see cref="FrameworkDiscoveryProperties"/>).
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
    /// Resolves the single effective TFM used ONLY to steer the CsWinRT metadata shim's ref-pack selection
    /// toward the project's <c>TargetPlatformVersion</c>. Unlike <see cref="ResolveEffectiveFrameworkAsync"/>
    /// this also covers a single-targeted project (whose <c>--framework</c> stays null) so an SDK-less host
    /// prefers the matching ref pack. Order: pinned/user <c>--framework</c>, cheap static read, then — only
    /// when the shim would inject (no registered SDK) and the value isn't static — an MSBuild evaluate. The
    /// result is NEVER used as a build <c>-p:TargetFramework</c>.
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
    /// Authoritatively resolves a project's effective SINGLE target framework via an evaluate-only MSBuild
    /// pass using the same globals as the real passes. Returns <c>TargetFramework</c> when set, else the
    /// first of <c>TargetFrameworks</c>, else <c>null</c> when the evaluate can't run.
    /// </summary>
    private async Task<string?> ResolveEvaluatedTargetFrameworkAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var args = BuildFrameworkDiscoveryArguments(csproj, options, FrameworkDiscoveryProperties);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));

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

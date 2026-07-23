// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Pure MSBuild argument and property-token construction for <see cref="ProjectRunService"/>'s restore,
/// build, and evaluate passes. Extracted into its own partial to keep the primary partial under the
/// repository's file-size limits; every method here is static and side-effect-free (unit-testable in
/// isolation).
/// </summary>
internal sealed partial class ProjectRunService
{
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
    /// level (Change #1).
    /// </summary>
    internal static string BuildBuildPassArguments(FileInfo csproj, ProjectRunOptions options, string verbosity, string? csWinRTMetadataFolder = null, bool nativeTerminal = false)
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

        // MSBuild terminal-logger regime for the build pass, gated on how winapp launches dotnet:
        //  • Redirected (streaming/json/quiet paths, nativeTerminal:false): pin `-tl:off`. winapp redirects
        //    dotnet's stdout, and the console logger is the only mode available under redirection; pinning
        //    off keeps clean append-only output and guards a future SDK that redefines `-tl:auto` and
        //    reintroduces the animated `(0.1s)(0.2s)…` carriage-return redraw churn.
        //  • Native terminal (nativeTerminal:true, inherited stdio on a real TTY): omit the token entirely
        //    so dotnet's default `-tl:auto` resolves to ON, giving the native live build display that
        //    de-duplicates warnings (the console logger double-prints them). Do NOT add `-tl:on` — absence
        //    is the correct default and lets dotnet honor a user's own TERM/redirection decisions.
        // Build pass only — the `--getProperty` evaluate pass is left untouched.
        if (!nativeTerminal)
        {
            tokens.Add("-tl:off");
        }

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
    /// emitted LAST so MSBuild's last-wins makes a dedicated value beat a conflicting user <c>-p</c>.
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
}

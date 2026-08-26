// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Single-file mode for <see cref="ProjectRunService" />: building and evaluating a .NET
/// <b>file-based app</b> — a single <c>.cs</c> the SDK compiles through a virtual
/// <c>&lt;stem&gt;.cs.csproj</c> configured by <c>#:</c> directives.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does NOT reuse the <c>.csproj</c> build/evaluate passes, for two reasons that are
/// both properties of the SDK rather than style choices:
/// </para>
/// <list type="number">
///   <item>
///   <b>The evaluate pass must be <c>dotnet build</c>, not <c>dotnet msbuild</c>.</b> MSBuild has no
///   <c>.cs</c> project loader — the virtual-project synthesis lives in the <c>dotnet build</c>/
///   <c>dotnet run</c> CLI path — so <c>dotnet msbuild counter.cs --getProperty:X</c> fails with
///   <c>MSB4025: The project file could not be loaded</c>. <c>dotnet build counter.cs --getProperty:X</c>
///   evaluates WITHOUT building, so the evaluate stays as cheap as the <c>.csproj</c> one.
///   </item>
///   <item>
///   <b>No RuntimeIdentifier or Platform is injected.</b> A file-based app declares its own
///   <c>TargetFramework</c>/<c>Platform</c> via <c>#:property</c>. Injecting <c>-r win-&lt;arch&gt;</c>
///   would move the build output away from the path the evaluate reports, so the two passes could
///   disagree about where the app is. The run handler rejects <c>--arch</c>/<c>--runtime</c>/
///   <c>--framework</c> and points at the equivalent <c>#:property</c> instead.
///   </item>
/// </list>
/// <para>
/// Both passes are otherwise fed IDENTICAL tokens (Configuration + user <c>-p</c>) so the evaluate reads
/// the output the build wrote.
/// </para>
/// </remarks>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// MSBuild properties requested from the single-file evaluate pass (always ≥2 → JSON output).
    /// Beyond the launch/packaging properties, this requests the five <c>WinApp*</c> manifest-inference
    /// properties plus <c>Version</c> and <c>WinAppManifestPath</c>. Undeclared properties evaluate to an
    /// empty string rather than being omitted, so the inference only needs null-or-empty checks.
    /// </summary>
    internal static readonly string[] SingleFileRequestedProperties =
    [
        "TargetDir",
        "OutputPath",
        "RunCommand",
        "AssemblyName",
        "OutputType",
        "WindowsPackageType",
        "WindowsAppSDKSelfContained",
        // The built TFM. Threaded into loose-layout runtime provisioning so the Windows App SDK version
        // is read from the framework the app was actually built for.
        "TargetFramework",
        // Architecture. Single-file mode rejects --arch and tells the user to declare
        // '#:property RuntimeIdentifier=win-x64' instead, so this is the only way the app's target
        // architecture reaches the Windows App Runtime provisioning that follows registration.
        // Platform is deliberately NOT consulted: a file-based app accepts it but ignores it for RID
        // selection, so 'Platform=arm64' still yields an x64 apphost.
        "RuntimeIdentifier",
        // Manifest inference. WinAppManifestPath mirrors the NuGet targets' escape hatch so a consumer can
        // point single-file mode at a hand-authored manifest from the .cs itself.
        "WinAppManifestPath",
        "WinAppPackageName",
        "WinAppDisplayName",
        "WinAppPublisher",
        "WinAppVersion",
        "WinAppDescription",
        // Read $(Version) — NOT $(VersionPrefix). Setting Version explicitly leaves VersionPrefix EMPTY,
        // so reading VersionPrefix first would silently discard the user's version.
        "Version",
    ];

    /// <inheritdoc />
    public Task<string?> CheckSingleFileSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
        => CheckSdkFloorAsync(
            workingDirectory,
            // 10.0.100 can BUILD a file-based app, but single-file mode also resolves the app's NuGet
            // packages with `dotnet package list --file`, and that option only exists from the 10.0.300
            // feature band. On an older band the discovery silently returns nothing, and a
            // framework-dependent Windows App SDK app loses the framework dependency it needs to launch.
            // Requiring the band that supports the whole flow beats degrading into a broken package.
            minimumMajor: 10,
            minimumPatch: 300,
            upgradeHint: "Running a .NET file-based app (a single .cs) requires .NET SDK 10.0.300 or newer. Install or update it from https://aka.ms/dotnet/download.",
            tooOldReason: "cannot resolve packages for .NET file-based apps",
            cancellationToken);

    /// <inheritdoc />
    public async Task<SingleFileBuildOutcome> BuildAndResolveSingleFileAsync(
        FileInfo singleFile,
        SingleFileRunOptions options,
        CancellationToken cancellationToken)
    {
        // MSBuildProjectDirectory for a file-based app is the .cs file's OWN directory (not the temp
        // output), so building from there keeps any Directory.Build.props next to the file in scope.
        var workingDir = singleFile.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        if (!options.NoBuild)
        {
            var buildExit = await RunSingleFileBuildPassAsync(singleFile, options, workingDir, cancellationToken);
            if (buildExit != 0)
            {
                // dotnet's diagnostics were already streamed live; log the summary and propagate the code.
                logger.LogError("{UISymbol} Build failed for {File} (exit code {ExitCode}).", UiSymbols.Error, singleFile.Name, buildExit);
                return new SingleFileBuildOutcome(null, buildExit);
            }
        }

        var evaluateArgs = BuildSingleFileEvaluateArguments(singleFile, options);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(evaluateArgs));

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);

        if (exitCode != 0)
        {
            logger.LogError("{UISymbol} Could not evaluate properties for {File} (exit code {ExitCode}).", UiSymbols.Error, singleFile.Name, exitCode);
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

            return new SingleFileBuildOutcome(null, exitCode);
        }

        var props = MsBuildPropertyReader.Parse(stdout, SingleFileRequestedProperties);

        var outputType = GetProp(props, "OutputType");
        if (!string.IsNullOrEmpty(outputType) && !ProjectDetectionService.IsExecutableOutputType(outputType))
        {
            throw new ProjectRunException(
                $"'{singleFile.Name}' is not a runnable app (OutputType='{outputType}'). Add '#:property OutputType=WinExe' (or 'Exe') to the file.");
        }

        // Single-file mode registers MSIX identity — that is the whole point of running a .cs through
        // winapp. An explicitly unpackaged app has no identity to register, and the unpackaged launch path
        // resolves the Windows App Runtime through 'dotnet list package' against a PROJECT file, which a
        // .cs has none of. Reject clearly rather than fall into a path that cannot work.
        if (string.Equals(GetProp(props, "WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectRunException(
                $"'{singleFile.Name}' declares WindowsPackageType=None, but 'winapp run' builds a .cs file-based app as a PACKAGED app with identity. " +
                "Remove '#:property WindowsPackageType=None' (and any -p:WindowsPackageType=None), or run the app without identity using 'dotnet run'.");
        }

        // TargetDir and OutputPath both evaluate to the same absolute, trailing-slash path for a
        // file-based app. Prefer TargetDir (matching project mode) and fall back to OutputPath.
        var outputDirectory = GetProp(props, "TargetDir");
        if (string.IsNullOrEmpty(outputDirectory))
        {
            outputDirectory = GetProp(props, "OutputPath");
        }

        if (string.IsNullOrEmpty(outputDirectory))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory for '{singleFile.Name}'. Ensure it builds successfully with 'dotnet build {singleFile.Name}'.");
        }

        outputDirectory = Path.GetFullPath(outputDirectory);

        if (!Directory.Exists(outputDirectory))
        {
            var reason = options.NoBuild
                ? "Remove --no-build so the app is built first."
                : $"Build it manually with 'dotnet build {singleFile.Name}' to see why.";
            throw new ProjectRunException(
                $"The build output directory for '{singleFile.Name}' does not exist ({outputDirectory}). {reason}");
        }

        var executableName = ResolveSingleFileExecutableName(props, singleFile, outputDirectory);

        return new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile,
                outputDirectory,
                executableName,
                ResolveSingleFileArchitecture(props),
                GetProp(props, "TargetFramework") is { Length: > 0 } tfm ? tfm : null,
                string.Equals(GetProp(props, "WindowsAppSDKSelfContained"), "true", StringComparison.OrdinalIgnoreCase),
                props),
            0);
    }

    /// <summary>
    /// Resolves the architecture the app was actually built for, so the Windows App Runtime installed
    /// after registration matches it.
    /// </summary>
    /// <remarks>
    /// Reads <c>RuntimeIdentifier</c> only. For a file-based app that is the sole property that changes
    /// the apphost's architecture — <c>#:property Platform=arm64</c> is accepted but leaves
    /// <c>RuntimeIdentifier</c> empty and still emits an x64 apphost on an x64 host, so treating
    /// <c>Platform</c> as the architecture would provision arm64 runtime packages for an x64 binary.
    /// With no RID the SDK builds for the host, which is what the fallback reports.
    /// </remarks>
    private static string ResolveSingleFileArchitecture(IReadOnlyDictionary<string, string> props)
        => RunArchHelper.ArchitectureFromRid(GetProp(props, "RuntimeIdentifier"))
            ?? RunArchHelper.DefaultArchitecture();

    /// <summary>
    /// Resolves the app executable's bare file name, written CONCRETELY into the generated manifest.
    /// <para>
    /// A <c>$targetnametoken$.exe</c> placeholder would be unusable here: every WinAppSDK self-contained
    /// output ships a <c>RestartAgent.exe</c> next to the app exe, so the token form always trips the
    /// "multiple .exe files found" ambiguity and would force <c>--executable</c> on every single run.
    /// </para>
    /// <c>RunCommand</c> is the authoritative apphost path when <c>UseAppHost</c> is on; otherwise fall
    /// back to <c>AssemblyName</c>.exe, and finally to the file stem.
    /// </summary>
    private static string ResolveSingleFileExecutableName(
        IReadOnlyDictionary<string, string> props,
        FileInfo singleFile,
        string outputDirectory)
    {
        var runCommand = GetProp(props, "RunCommand");
        if (!string.IsNullOrEmpty(runCommand) &&
            runCommand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            Path.IsPathRooted(runCommand))
        {
            return Path.GetFileName(runCommand);
        }

        var assemblyName = GetProp(props, "AssemblyName");

        // Normalize to a bare file name. Both the RunCommand branch above and AssemblyName come from
        // MSBuild properties the .cs file controls, and this value is used TWICE: to probe the output
        // directory, and (via the caller) as the manifest's Executable attribute. A rooted or
        // separator-bearing value would make Path.Combine silently discard outputDirectory and would put
        // a path where the manifest schema expects a package-relative file name.
        var candidate = Path.GetFileName(
            !string.IsNullOrEmpty(assemblyName)
                ? assemblyName + ".exe"
                : Path.GetFileNameWithoutExtension(singleFile.Name) + ".exe");

        if (string.IsNullOrEmpty(candidate) || !File.Exists(Path.Join(outputDirectory, candidate)))
        {
            throw new ProjectRunException(
                $"Could not find the application executable '{candidate}' in the build output ({outputDirectory}). " +
                $"Ensure '{singleFile.Name}' declares '#:property OutputType=WinExe' (or 'Exe') and builds an apphost.");
        }

        return candidate;
    }

    /// <summary>
    /// Runs the single-file BUILD pass. Mirrors <see cref="RunBuildPassAsync"/>'s output regime exactly —
    /// stderr under <c>--json</c>/<c>--quiet</c>, inherited stdio (native terminal logger) on a real TTY,
    /// live line streaming otherwise — so a <c>.cs</c> build looks and behaves like a <c>.csproj</c> build.
    /// </summary>
    private async Task<int> RunSingleFileBuildPassAsync(
        FileInfo singleFile,
        SingleFileRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);
        var banner = $"Building {singleFile.Name} ({options.Configuration})...";
        var stopwatch = Stopwatch.StartNew();

        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            var redirectedArgs = BuildSingleFileBuildPassArguments(singleFile, options, verbosity);
            if (options.Json)
            {
                Console.Error.WriteLine($"dotnet {RedactSecretsForDisplay(redirectedArgs)}");
            }
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, redirectedArgs,
                onOutputLine: static line => Console.Error.WriteLine(line),
                onErrorLine: static line => Console.Error.WriteLine(line),
                cancellationToken);
        }

        var nativeTerminal = NativeTerminalGateOverrideForTests?.Invoke()
            ?? ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);
        var buildArgs = BuildSingleFileBuildPassArguments(singleFile, options, verbosity, nativeTerminal);
        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} {banner}");
        ansiConsole.MarkupLineInterpolated($"[dim]   dotnet {Markup.Escape(RedactSecretsForDisplay(buildArgs))}[/]");

        int streamedExit;
        if (nativeTerminal)
        {
            streamedExit = await dotNetService.RunDotnetInheritedAsync(workingDir, buildArgs, cancellationToken);
        }
        else
        {
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
            ansiConsole.MarkupLineInterpolated(
                $"{UiSymbols.Check} Built {Path.GetFileNameWithoutExtension(singleFile.Name)} in {stopwatch.Elapsed.TotalSeconds:0.0}s");
        }

        return streamedExit;
    }
}



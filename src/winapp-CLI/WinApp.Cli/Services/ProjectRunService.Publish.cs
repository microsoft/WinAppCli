// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>Operation-aware build/publish preparation for project-mode runs.</summary>
internal sealed partial class ProjectRunService
{
    /// <inheritdoc />
    public async Task<ProjectPreparationOutcome> PrepareAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        ProjectPreparationOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation == ProjectPreparationOperation.Publish)
        {
            return await PublishAndResolveAsync(csproj, options, cancellationToken);
        }

        if (options.DryRun)
        {
            return await EvaluateBuildDryRunAsync(csproj, options, cancellationToken);
        }

        var build = await BuildAndResolveCoreAsync(csproj, options, cancellationToken);
        var resolution = build.Resolution;
        if (resolution is not null && string.IsNullOrWhiteSpace(resolution.DotnetSdk))
        {
            var workingDirectory = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            resolution = resolution with
            {
                DotnetSdk = await ResolveDotnetSdkVersionAsync(
                    workingDirectory,
                    cancellationToken),
            };
        }
        return new ProjectPreparationOutcome(
            resolution,
            build.ExitCode,
            Executed: true,
            Ready: resolution is not null,
            ErrorCode: resolution is null ? "BuildFailed" : null,
            Error: resolution is null ? $"Build failed (exit code {build.ExitCode})." : null);
    }

    /// <summary>
    /// Compatibility wrapper retained for focused service tests and internal callers that explicitly
    /// request the original build operation.
    /// </summary>
    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var outcome = await PrepareAndResolveAsync(
            csproj,
            options with { DryRun = false },
            ProjectPreparationOperation.Build,
            cancellationToken);
        return new ProjectBuildOutcome(outcome.Resolution, outcome.ExitCode);
    }

    private async Task<ProjectPreparationOutcome> EvaluateBuildDryRunAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);
        options = ResolvePlatformInjection(csproj, options);
        var shimFramework = await ResolveShimFrameworkAsync(csproj, options, workingDir, cancellationToken);
        var shim = ResolveCsWinRTMetadataShim(options, shimFramework);
        var evaluateArgs = BuildEvaluateArguments(csproj, options, shim);

        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(evaluateArgs));
        var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(
            workingDir,
            evaluateArgs,
            cancellationToken);

        if (exitCode != 0)
        {
            var restore = $"dotnet {BuildDryRunRestoreArguments(csproj, options, publishAot: false)}";
            return new ProjectPreparationOutcome(
                null,
                exitCode,
                Executed: false,
                Ready: null,
                Reason: "RestoreRequired",
                SuggestedCommand: restore,
                ErrorCode: "RestoreRequired",
                Error: "Project evaluation is incomplete because restored assets are unavailable.");
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);
        ValidateExecutableOutputType(csproj, props);
        var targetDir = ResolveAbsolutePath(GetProp(props, "TargetDir"), workingDir);
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory (TargetDir) for '{csproj.Name}'.");
        }

        var resolution = new ProjectRunResolution(
            csproj,
            targetDir,
            NullIfEmpty(GetProp(props, "RunCommand")),
            DeterminePackaging(props, targetDir),
            IsTrue(GetProp(props, "WindowsAppSDKSelfContained")),
            options.Architecture,
            options.Framework,
            options.NoRestore,
            NullIfEmpty(GetProp(props, "RunArguments")),
            Operation: ProjectPreparationOperation.Build,
            RuntimeIdentifier: RunArchHelper.ToRuntimeIdentifier(options.Architecture),
            SourceExecutable: NullIfEmpty(GetProp(props, "RunCommand")),
            ProjectAssetsFile: ResolveAbsolutePath(GetProp(props, "ProjectAssetsFile"), workingDir),
            DotnetSdk: await ResolveDotnetSdkVersionAsync(workingDir, cancellationToken),
            EvaluatedPlatform: NullIfEmpty(GetProp(props, "Platform")) ?? options.Platform,
            BundledNetCoreAppPackageVersion: NullIfEmpty(
                GetProp(props, "BundledNETCoreAppPackageVersion")));

        return new ProjectPreparationOutcome(
            resolution,
            0,
            Executed: false,
            Ready: true);
    }

    private async Task<ProjectPreparationOutcome> PublishAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);
        options = ResolvePlatformInjection(csproj, options) with
        {
            // Publish always targets a concrete RID. In particular, Native AOT cannot be evaluated or
            // produced from a Platform-only outer build.
            OmitRuntimeIdentifier = false,
        };

        var shimFramework = await ResolveShimFrameworkAsync(csproj, options, workingDir, cancellationToken);
        var initialShim = ResolveCsWinRTMetadataShim(options, shimFramework);
        var initialEvaluateArgs = BuildEvaluateArguments(
            csproj,
            options,
            initialShim,
            forceRuntimeIdentifier: true);

        logger.LogDebug(
            "{UISymbol} Native publish preflight evaluation: dotnet {Arguments}",
            UiSymbols.Note,
            RedactSecretsForDisplay(initialEvaluateArgs));

        var (initialExit, initialStdout, initialStderr) = await dotNetService.RunDotnetCommandAsync(
            workingDir,
            initialEvaluateArgs,
            cancellationToken);

        IReadOnlyDictionary<string, string>? initialProperties = null;
        if (initialExit == 0)
        {
            initialProperties = MsBuildPropertyReader.Parse(initialStdout, RequestedProperties);
        }

        var publishAot = initialProperties is not null
            ? IsTrue(GetProp(initialProperties, "PublishAot"))
            : TryReadBooleanProjectProperty(csproj, options.Properties, "PublishAot");

        if (options.VerifyNativeAot && initialProperties is not null && !publishAot)
        {
            const string error =
                "--verify-native-aot requires the evaluated publish to set PublishAot=true. Add '<PublishAot>true</PublishAot>' to the project or pass '-p PublishAot=true'.";
            return FailedPreparation("PublishAotRequired", error, executed: false);
        }

        WindowsNativeToolchain? toolchain = null;
        if (publishAot || options.VerifyNativeAot)
        {
            if (!TryGetNativeArchitecture(options.Architecture, out var nativeArchitecture))
            {
                return FailedPreparation(
                    "UnsupportedNativeAotArchitecture",
                    $"Windows Native AOT publishing supports win-x64 and win-arm64. Runtime 'win-{options.Architecture}' is not supported.",
                    executed: false);
            }

            var toolchainResolution = await nativeToolchainResolver.ResolveAsync(
                new WindowsNativeToolchainRequirements(
                    nativeArchitecture,
                    RequireCompiler: false,
                    RequireLinker: true,
                    RequireWindowsSdk: true),
                cancellationToken);

            if (!toolchainResolution.Succeeded)
            {
                var error = FormatToolchainFailure(csproj, options, toolchainResolution);
                return FailedPreparation(
                    toolchainResolution.ErrorCode ?? "NativeAotToolchainUnavailable",
                    error,
                    executed: false);
            }

            toolchain = toolchainResolution.Toolchain;
            PrintNativeToolchain(toolchain!, options);
        }

        var dotnetSdk = await ResolveDotnetSdkVersionAsync(workingDir, cancellationToken);

        if (options.DryRun)
        {
            if (initialProperties is null)
            {
               return RestoreRequiredOutcome(
                   csproj,
                   options,
                   publishAot || options.VerifyNativeAot,
                   initialExit,
                   initialStderr);
            }

            ProjectRunResolution resolution;
            try
            {
                resolution = CreatePublishResolution(
                    csproj,
                    options,
                    initialProperties,
                    workingDir,
                    requireArtifacts: false,
                    dotnetSdk,
                    toolchain);
            }
            catch (ProjectRunException ex)
            {
                return FailedPreparation("PublishPlanInvalid", ex.Message, executed: false);
            }

            if (publishAot && !NativeAotRuntimePacksAreAvailable(resolution))
            {
                return RestoreRequiredOutcome(csproj, options, publishAot, 1, null, resolution);
            }

            return new ProjectPreparationOutcome(
                resolution,
                0,
                Executed: false,
                Ready: true);
        }

        // Toolchain preflight above intentionally precedes any restore. Once it succeeds, reuse the normal
        // framework/shim/solution preparation so project-mode publish keeps build-mode restore behavior.
        var (resolvedOptions, publishOptions, csWinRTMetadata) = await PrepareBuildInputsAsync(
            csproj,
            options,
            workingDir,
            setStatus: null,
            cancellationToken);
        resolvedOptions = resolvedOptions with { OmitRuntimeIdentifier = false };
        publishOptions = publishOptions with { OmitRuntimeIdentifier = false };

        var publishResult = await RunPublishPassAsync(
            csproj,
            publishOptions,
            workingDir,
            csWinRTMetadata,
            toolchain?.Environment,
            cancellationToken);
        if (publishResult.ExitCode != 0)
        {
            logger.LogError(
                "{UISymbol} Publish failed for {Project} (exit code {ExitCode}).",
                UiSymbols.Error,
                csproj.Name,
                publishResult.ExitCode);
            return FailedPreparation(
                "PublishFailed",
                $"Publish failed for '{csproj.Name}' (exit code {publishResult.ExitCode}).",
                executed: true,
                exitCode: publishResult.ExitCode);
        }

        if (options.VerifyNativeAot &&
           TryFindNativeAotDiagnostic(publishResult.Output, publishResult.Error, out var nativeAotDiagnostic))
        {
           return FailedPreparation(
               "NativeAotPublishDiagnostics",
               $"Native publish completed with an AOT or trimming diagnostic that must be resolved before verification can succeed: {nativeAotDiagnostic}",
               executed: true);
        }
        if (options.VerifyNativeAot)
        {
           logger.LogDebug(
               "{UISymbol} dotnet publish exited 0; no blocking Native AOT or trimming diagnostics were classified.",
               UiSymbols.Note);
        }
        else
        {
           logger.LogDebug("{UISymbol} dotnet publish exited 0.", UiSymbols.Note);
        }

        var evaluateArgs = BuildEvaluateArguments(
            csproj,
            resolvedOptions,
            csWinRTMetadata,
            forceRuntimeIdentifier: true);
        logger.LogDebug(
            "{UISymbol} Post-publish evaluation: dotnet {Arguments}",
            UiSymbols.Note,
            RedactSecretsForDisplay(evaluateArgs));

        var (evaluateExit, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(
            workingDir,
            evaluateArgs,
            cancellationToken);
        if (evaluateExit != 0)
        {
            WriteEvaluationDiagnostics(resolvedOptions.Json, stdout, stderr);
            return FailedPreparation(
                "PublishDirUnresolved",
                $"Publish completed, but PublishDir could not be evaluated for '{csproj.Name}'.",
                executed: true,
                exitCode: evaluateExit);
        }

        var properties = MsBuildPropertyReader.Parse(stdout, RequestedProperties);
        if (options.VerifyNativeAot && !IsTrue(GetProp(properties, "PublishAot")))
        {
            return FailedPreparation(
                "PublishAotRequired",
                "Publish completed, but the evaluated publish did not set PublishAot=true.",
                executed: true);
        }

        ProjectRunResolution finalResolution;
        try
        {
            finalResolution = CreatePublishResolution(
                csproj,
                resolvedOptions,
                properties,
                workingDir,
                requireArtifacts: true,
                dotnetSdk,
                toolchain);
        }
        catch (ProjectRunException ex)
        {
           return FailedPreparation(
               ClassifyPublishedArtifactError(ex.Message),
               ex.Message,
               executed: true);
        }

        logger.LogDebug(
           "{UISymbol} Publish evaluation: PublishAot={PublishAot}; PublishDir={PublishDir}; Packaging={Packaging}; Manifest={Manifest}; RID={RuntimeIdentifier}; Platform={Platform}; SourceExecutable={SourceExecutable}",
           UiSymbols.Note,
           finalResolution.PublishAot,
           finalResolution.PublishDirectory,
           finalResolution.Packaging,
           finalResolution.FinalAppxManifestPath,
           finalResolution.RuntimeIdentifier,
           finalResolution.EvaluatedPlatform,
           finalResolution.SourceExecutable);

        if (!options.Json && logger.IsEnabled(LogLevel.Information))
        {
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Native publish completed");
            ansiConsole.MarkupLineInterpolated($"  Output: {finalResolution.PublishDirectory}");
        }

        return new ProjectPreparationOutcome(
            finalResolution,
            0,
            Executed: true,
            Ready: true);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunPublishPassAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        string? csWinRTMetadata,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var arguments = BuildPublishPassArguments(
            csproj,
            options,
            ResolveBuildVerbosity(logger, options.Json),
            csWinRTMetadata);
        var display = WindowsCommandLine.JoinArguments(arguments) ?? string.Empty;

        Action<string>? stdout = null;
        Action<string>? stderr = null;
        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            stdout = static line => Console.Error.WriteLine(line);
            stderr = static line => Console.Error.WriteLine(line);
            if (options.Json)
            {
                Console.Error.WriteLine($"dotnet {RedactSecretsForDisplay(display)}");
            }
        }
        else
        {
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} Publishing...");
            if (logger.IsEnabled(LogLevel.Debug))
            {
                ansiConsole.MarkupLineInterpolated(
                    $"[dim]   dotnet {Markup.Escape(RedactSecretsForDisplay(display))}[/]");
            }

            var writeLock = new object();
            void WriteLive(string line)
            {
                lock (writeLock)
                {
                    ansiConsole.WriteLine(line);
                }
            }

            stdout = WriteLive;
            stderr = WriteLive;
        }

        return await dotNetService.RunDotnetCommandAsync(
            workingDir,
            arguments,
            environment,
            stdout,
            stderr,
            cancellationToken);
    }

    private static ProjectRunResolution CreatePublishResolution(
        FileInfo csproj,
        ProjectRunOptions options,
        IReadOnlyDictionary<string, string> properties,
        DirectoryInfo workingDir,
        bool requireArtifacts,
        string? dotnetSdk,
        WindowsNativeToolchain? toolchain)
    {
       ValidateExecutableOutputType(csproj, properties);

        var targetDir = ResolveAbsolutePath(GetProp(properties, "TargetDir"), workingDir);
        var publishDirectory = ResolveAbsolutePath(GetProp(properties, "PublishDir"), workingDir);
        if (string.IsNullOrWhiteSpace(publishDirectory))
        {
            throw new ProjectRunException(
                $"Could not resolve PublishDir for '{csproj.Name}'. Ensure the project and publish profile evaluate successfully.");
        }

        if (requireArtifacts && !Directory.Exists(publishDirectory))
        {
            throw new ProjectRunException(
                $"The evaluated PublishDir does not exist: '{publishDirectory}'. The publish did not produce the selected output.");
        }

        var packaging = DeterminePackaging(properties, targetDir);
        var finalManifest = packaging == ProjectPackaging.Packaged
            ? ResolveFinalAppxManifest(
                GetProp(properties, "FinalAppxManifestName"),
                targetDir,
                workingDir,
                requireArtifacts)
            : null;

        var sourceExecutable = ResolvePublishedExecutable(
            csproj,
            publishDirectory,
            properties,
            finalManifest,
            requireArtifacts);
        var publishAot = IsTrue(GetProp(properties, "PublishAot"));

        string? runCommand = sourceExecutable;
        string? runArguments = null;
        if (!File.Exists(sourceExecutable) && !publishAot)
        {
            var targetFileName = GetProp(properties, "TargetFileName");
            var managedAssemblyFileName = Path.GetFileName(targetFileName);
            var managedAssembly =
                string.IsNullOrWhiteSpace(targetFileName) ||
                !string.Equals(
                    managedAssemblyFileName,
                    targetFileName,
                    StringComparison.Ordinal)
                    ? null
                    : Path.GetFullPath(managedAssemblyFileName, publishDirectory);
            if (managedAssembly is not null && File.Exists(managedAssembly))
            {
                runCommand = "dotnet";
                runArguments = WindowsCommandLine.JoinArguments(["exec", managedAssembly]);
            }
        }

        if (requireArtifacts && packaging == ProjectPackaging.Unpackaged &&
            (string.IsNullOrWhiteSpace(runCommand) || !RunCommandIsLaunchable(runCommand)))
        {
            throw new ProjectRunException(
                $"'{csproj.Name}' published as an unpackaged app, but its executable was not found in PublishDir '{publishDirectory}'.");
        }

        return new ProjectRunResolution(
            csproj,
            targetDir,
            runCommand,
            packaging,
            IsTrue(GetProp(properties, "WindowsAppSDKSelfContained")),
            options.Architecture,
            options.Framework,
            options.NoRestore,
            runArguments,
            Operation: ProjectPreparationOperation.Publish,
            PublishDirectory: publishDirectory,
            PublishAot: publishAot,
            RuntimeIdentifier: NullIfEmpty(GetProp(properties, "RuntimeIdentifier"))
                ?? RunArchHelper.ToRuntimeIdentifier(options.Architecture),
            SourceExecutable: sourceExecutable,
            FinalAppxManifestPath: finalManifest,
            ProjectAssetsFile: ResolveAbsolutePath(GetProp(properties, "ProjectAssetsFile"), workingDir),
            DotnetSdk: dotnetSdk,
            NativeToolchain: toolchain,
            PublishProfile: NullIfEmpty(GetProp(properties, "PublishProfile")),
            EvaluatedPlatform: NullIfEmpty(GetProp(properties, "Platform")) ?? options.Platform,
            BundledNetCoreAppPackageVersion: NullIfEmpty(
                GetProp(properties, "BundledNETCoreAppPackageVersion")));
    }

    private static string ResolvePublishedExecutable(
        FileInfo csproj,
        string publishDirectory,
        IReadOnlyDictionary<string, string> properties,
        string? finalManifest,
        bool requireArtifacts)
    {
        if (!string.IsNullOrWhiteSpace(finalManifest) && File.Exists(finalManifest))
        {
            try
            {
                var manifest = AppxManifestDocument.Load(finalManifest);
                if (!string.IsNullOrWhiteSpace(manifest.ApplicationExecutable))
                {
                    var manifestExecutable = Path.GetFullPath(
                        manifest.ApplicationExecutable.Replace('/', Path.DirectorySeparatorChar),
                        publishDirectory);
                    if (!requireArtifacts || File.Exists(manifestExecutable))
                    {
                        return manifestExecutable;
                    }
                }
            }
            catch (System.Xml.XmlException ex)
            {
                throw new ProjectRunException(
                    $"The generated AppxManifest.xml could not be parsed: {ex.Message}");
            }
        }

        var targetName = NullIfEmpty(GetProp(properties, "TargetName"))
            ?? NullIfEmpty(GetProp(properties, "AssemblyName"))
            ?? Path.GetFileNameWithoutExtension(csproj.Name);
        var safeTargetName = Path.GetFileName(targetName);
        if (!string.Equals(safeTargetName, targetName, StringComparison.Ordinal))
        {
            throw new ProjectRunException(
                $"The evaluated target name '{targetName}' is not a valid file name.");
        }

        var executableName = safeTargetName + ".exe";
        var executable = Path.GetFullPath(executableName, publishDirectory);
        if (requireArtifacts && !File.Exists(executable))
        {
            throw new ProjectRunException(
                $"The published executable '{executable}' was not found. PublishDir was resolved from the same publish invocation; stale build output was not selected.");
        }
        return executable;
    }

    private static string? ResolveFinalAppxManifest(
        string evaluatedName,
        string targetDir,
        DirectoryInfo workingDir,
        bool requireArtifact)
    {
        if (string.IsNullOrWhiteSpace(evaluatedName))
        {
            if (requireArtifact)
            {
                throw new ProjectRunException(
                    "The project is packaged, but FinalAppxManifestName did not resolve to an MSBuild-generated manifest.");
            }
            return null;
        }

        if (Path.IsPathFullyQualified(evaluatedName))
        {
            var rooted = Path.GetFullPath(evaluatedName);
            if (requireArtifact && !File.Exists(rooted))
            {
                throw new ProjectRunException($"The generated AppxManifest.xml was not found at '{rooted}'.");
            }
            return rooted;
        }

        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(targetDir) ? null : Path.GetFullPath(evaluatedName, targetDir),
            Path.GetFullPath(evaluatedName, workingDir.FullName),
        }.Where(path => path is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is not null)
        {
            return existing;
        }

        if (requireArtifact)
        {
            throw new ProjectRunException(
                $"The generated AppxManifest.xml '{evaluatedName}' was not found. Searched: {string.Join(", ", candidates)}.");
        }
        return candidates.FirstOrDefault();
    }

    internal static bool NativeAotRuntimePacksAreAvailable(ProjectRunResolution resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution.ProjectAssetsFile) ||
            !File.Exists(resolution.ProjectAssetsFile))
        {
            return false;
        }

        try
        {
            using var assets = JsonDocument.Parse(File.ReadAllText(resolution.ProjectAssetsFile));
            var rid = resolution.RuntimeIdentifier ?? RunArchHelper.ToRuntimeIdentifier(resolution.Architecture);
            var runtimePackId = $"Microsoft.NETCore.App.Runtime.NativeAOT.{rid}";
            var compilerPackId = $"runtime.{rid}.Microsoft.DotNet.ILCompiler";
            var version = resolution.BundledNetCoreAppPackageVersion
                ?? FindPackageVersion(assets.RootElement, runtimePackId)
                ?? FindPackageVersion(assets.RootElement, compilerPackId);
            if (string.IsNullOrWhiteSpace(version) ||
                !assets.RootElement.TryGetProperty("packageFolders", out var packageFolders) ||
                packageFolders.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return packageFolders.EnumerateObject().Any(packageFolder =>
                IsCompletePackage(packageFolder.Name, runtimePackId, version) &&
                IsCompletePackage(packageFolder.Name, compilerPackId, version));
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return false;
        }
    }

    private static string? FindPackageVersion(JsonElement assets, string packageId)
    {
        if (assets.TryGetProperty("libraries", out var libraries) &&
            libraries.ValueKind == JsonValueKind.Object)
        {
            var prefix = packageId + "/";
            var library = libraries.EnumerateObject().FirstOrDefault(property =>
                property.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (library.Name is not null)
            {
                return library.Name[prefix.Length..];
            }
        }

        if (!assets.TryGetProperty("project", out var project) ||
            !project.TryGetProperty("frameworks", out var frameworks) ||
            frameworks.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var framework in frameworks.EnumerateObject())
        {
            if (!framework.Value.TryGetProperty("downloadDependencies", out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (!dependency.TryGetProperty("name", out var name) ||
                    !string.Equals(name.GetString(), packageId, StringComparison.OrdinalIgnoreCase) ||
                    !dependency.TryGetProperty("version", out var versionElement))
                {
                    continue;
                }

                var range = versionElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(range) &&
                    range.StartsWith('[') &&
                    range.EndsWith(']'))
                {
                    return range[1..^1]
                        .Split(',', StringSplitOptions.TrimEntries)
                        .FirstOrDefault();
                }
            }
        }

        return null;
    }

    private static bool IsCompletePackage(string packageFolder, string packageId, string version)
    {
        if (!Path.IsPathFullyQualified(packageFolder))
        {
            return false;
        }

        var safePackageId = Path.GetFileName(packageId).ToLowerInvariant();
        var safeVersion = Path.GetFileName(version).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safePackageId) ||
            string.IsNullOrWhiteSpace(safeVersion) ||
            !string.Equals(safePackageId, packageId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(safeVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var packageDirectory = Path.GetFullPath(
            $"{safePackageId}{Path.DirectorySeparatorChar}{safeVersion}",
            packageFolder);
        return Directory.Exists(packageDirectory) &&
               File.Exists(Path.GetFullPath(".nupkg.metadata", packageDirectory));
    }

    private static bool TryFindNativeAotDiagnostic(
       string standardOutput,
       string standardError,
       out string diagnostic)
    {
       diagnostic = string.Empty;
       var lines = string.Join(Environment.NewLine, standardOutput, standardError)
           .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
       var match = lines.FirstOrDefault(line =>
           line.Contains("warning IL2", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("warning IL3", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("warning IL4", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("AOT analysis warning", StringComparison.OrdinalIgnoreCase));
       if (match is null)
       {
           return false;
       }

       diagnostic = match;
       return true;
    }

    private static bool TryReadBooleanProjectProperty(
        FileInfo csproj,
        IReadOnlyList<string> userProperties,
        string name)
    {
        if (TryGetUserProperty(userProperties, name, out var userValue))
        {
            return IsTrue(userValue);
        }

        try
        {
            var document = System.Xml.Linq.XDocument.Load(csproj.FullName);
            return document.Descendants()
                .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Value)
                .Any(IsTrue);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string?> ResolveDotnetSdkVersionAsync(
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(
                workingDir,
                "--version",
                cancellationToken);
            return exitCode == 0
                ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private void PrintNativeToolchain(
        WindowsNativeToolchain toolchain,
        ProjectRunOptions options)
    {
        if (!options.Json && logger.IsEnabled(LogLevel.Information))
        {
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Search} Checking native toolchain");
            ansiConsole.MarkupLineInterpolated($"  {UiSymbols.Check} Visual Studio Build Tools {toolchain.VisualStudioVersion}");
            ansiConsole.MarkupLineInterpolated($"  {UiSymbols.Check} MSVC {options.Architecture} linker {toolchain.VcToolsVersion}");
            ansiConsole.MarkupLineInterpolated($"  {UiSymbols.Check} Windows SDK {toolchain.WindowsSdkVersion}");
        }

        logger.LogDebug(
           "{UISymbol} Native toolchain: component={Component}; VS={VisualStudioPath}; MSVC={Msvc}; compiler={Compiler}; linker={Linker}; SDK={Sdk}; environment modifications={EnvironmentKeys}",
            UiSymbols.Note,
           options.Architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase)
               ? "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
               : "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            toolchain.VisualStudioInstallPath,
            toolchain.VcToolsVersion,
           toolchain.CompilerPath ?? "(not requested)",
            toolchain.LinkerPath,
            toolchain.WindowsSdkVersion,
            string.Join(", ", toolchain.Environment.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)));
    }

    private static ProjectPreparationOutcome RestoreRequiredOutcome(
        FileInfo csproj,
        ProjectRunOptions options,
        bool publishAot,
        int exitCode,
        string? evaluationError,
        ProjectRunResolution? resolution = null)
    {
        var restore = $"dotnet {BuildDryRunRestoreArguments(csproj, options, publishAot)}";
        var detail = string.IsNullOrWhiteSpace(evaluationError)
            ? "The Native AOT runtime pack is not present in the restored project assets."
            : $"Project evaluation was incomplete: {evaluationError.Trim()}";
        return new ProjectPreparationOutcome(
            resolution,
            exitCode == 0 ? 1 : exitCode,
            Executed: false,
            Ready: null,
            Reason: "RestoreRequired",
            SuggestedCommand: restore,
            ErrorCode: "RestoreRequired",
            Error: $"{detail} Run the suggested restore command, then repeat the dry run.");
    }

    private static ProjectPreparationOutcome FailedPreparation(
        string errorCode,
        string error,
        bool executed,
        int exitCode = 1) =>
        new(
            null,
            exitCode == 0 ? 1 : exitCode,
            Executed: executed,
            Ready: false,
            ErrorCode: errorCode,
            Error: error);

    private static string ClassifyPublishedArtifactError(string error)
    {
        if (error.Contains("PublishDir", StringComparison.OrdinalIgnoreCase))
        {
               return "PublishDirUnresolved";
        }
        if (error.Contains("executable", StringComparison.OrdinalIgnoreCase))
        {
               return "PublishedExecutableMissing";
        }
        if (error.Contains("AppxManifest", StringComparison.OrdinalIgnoreCase))
        {
               return "GeneratedManifestMissing";
        }
        return "PublishedArtifactInvalid";
    }

    private static string FormatToolchainFailure(
        FileInfo csproj,
        ProjectRunOptions options,
        WindowsNativeToolchainResolution failure)
    {
        var retry = new List<string>
        {
            "winapp",
            "run",
            csproj.FullName,
            "--publish",
            "--verify-native-aot",
            "-c",
            options.Configuration,
            "-r",
            RunArchHelper.ToRuntimeIdentifier(options.Architecture),
        };
        var lines = new List<string>
        {
            failure.Error ?? "The Windows Native AOT toolchain is unavailable.",
        };
        if (!string.IsNullOrWhiteSpace(failure.RequiredComponent))
        {
            lines.Add(string.Empty);
            lines.Add("Install component:");
            lines.Add($"  {failure.RequiredComponent}");
        }
        lines.Add(string.Empty);
        lines.Add("Then retry:");
        lines.Add($"  {WindowsCommandLine.JoinArguments(retry)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool TryGetNativeArchitecture(string architecture, out Architecture nativeArchitecture)
    {
        switch (architecture.ToLowerInvariant())
        {
            case "x64":
                nativeArchitecture = Architecture.X64;
                return true;
            case "arm64":
                nativeArchitecture = Architecture.Arm64;
                return true;
            default:
                nativeArchitecture = default;
                return false;
        }
    }

    private static string ResolveAbsolutePath(string path, DirectoryInfo baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        return Path.GetFullPath(path, baseDirectory.FullName);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ValidateExecutableOutputType(
       FileInfo csproj,
       IReadOnlyDictionary<string, string> properties)
    {
       var outputType = GetProp(properties, "OutputType");
       if (!string.IsNullOrWhiteSpace(outputType) &&
           !ProjectDetectionService.IsExecutableOutputType(outputType))
       {
           throw new ProjectRunException(
               $"'{csproj.Name}' is not a runnable project (OutputType='{outputType}'). 'winapp run' requires an executable project (OutputType Exe or WinExe).");
       }
    }

    private static bool IsTrue(string value) =>
        string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private void WriteEvaluationDiagnostics(bool json, params string[] diagnostics)
    {
        var combined = string.Join(
            Environment.NewLine,
            diagnostics
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.TrimEnd()));
        if (combined.Length == 0)
        {
            return;
        }

        if (json)
        {
            Console.Error.WriteLine(combined);
        }
        else
        {
            ansiConsole.WriteLine(combined);
        }
    }
}

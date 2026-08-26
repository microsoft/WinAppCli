// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// Single-file mode for <c>winapp run</c>: building a .NET <b>file-based app</b> (a single <c>.cs</c>
/// configured by <c>#:</c> directives) and running it as a packaged app with identity.
/// </summary>
/// <remarks>
/// The mode is a thin front-end. It builds the file, evaluates its MSBuild properties, resolves or
/// synthesizes an appxmanifest into the build output, and then hands off to the <b>unchanged</b> shared
/// folder pipeline (<c>ExecuteRunPipelineAsync</c>) — a file-based app's build output is structurally
/// identical to any other WinUI loose layout, so the only thing it was ever missing was a manifest.
/// </remarks>
internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Options that describe how to build a <c>.csproj</c> and have no meaning for a file-based app,
        /// mapped to the <c>#:</c> directive that replaces them. A file-based app declares its own target
        /// framework and platform inline, and winapp must not inject a RuntimeIdentifier or Platform
        /// because either would relocate the build output away from the path the evaluate pass reads back.
        /// </summary>
        private static readonly (Option Option, string Name, string Replacement)[] SingleFileRejectedOptions =
        [
            (ProjectOption, "--project", "A .cs file-based app IS the project; omit --project."),
            (FrameworkOption, "--framework", "Declare the target framework in the file instead, e.g. '#:property TargetFramework=net10.0-windows10.0.22621.0'."),
            (ArchOption, "--arch", "Declare the architecture in the file instead, e.g. '#:property Platform=x64'."),
            (RuntimeOption, "--runtime", "Declare the architecture in the file instead, e.g. '#:property Platform=x64'."),
        ];

        /// <summary>
        /// Single-file mode entry point: build the <c>.cs</c>, resolve its evaluated properties, resolve or
        /// generate the manifest, then launch through the shared folder pipeline.
        /// </summary>
        private async Task<int> RunSingleFileModeAsync(
            ParseResult parseResult,
            FileInfo singleFile,
            string? appArgs,
            bool isJson,
            CancellationToken cancellationToken)
        {
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var noBuild = parseResult.GetValue(NoBuildOption);
            var noRestore = parseResult.GetValue(NoRestoreOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];

            var noLaunch = parseResult.GetValue(NoLaunchOption);
            var withAlias = parseResult.GetValue(WithAliasOption);
            var debugOutput = parseResult.GetValue(DebugOutputOption);
            var unregisterOnExit = parseResult.GetValue(UnregisterOnExitOption);
            var detach = parseResult.GetValue(DetachOption);
            var clean = parseResult.GetValue(CleanOption);
            var useSymbols = parseResult.GetValue(SymbolsOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var manifest = parseResult.GetValue(ManifestOption);
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);

            // Reject project-only build knobs, pointing at the #: directive that replaces each.
            foreach (var (option, name, replacement) in SingleFileRejectedOptions)
            {
                if (parseResult.GetResult(option) is not null)
                {
                    return Fail($"{name} does not apply to '{singleFile.Name}'. {replacement}", isJson);
                }
            }

            if (!TryValidateProperties(properties, isJson, out var propertyError))
            {
                return propertyError;
            }

            if (!isJson && logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Search} {singleFile.Name}  ·  {configuration}  ·  file-based app");
            }

            // Building a bare .cs through a virtual project requires .NET 10 — a higher floor than the
            // 8.0.100 that --getProperty alone needs.
            var workingDir = singleFile.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
            var sdkError = await projectRunService.CheckSingleFileSdkAsync(workingDir, cancellationToken);
            if (sdkError != null)
            {
                return Fail(sdkError, isJson);
            }

            SingleFileBuildOutcome outcome;
            try
            {
                outcome = await projectRunService.BuildAndResolveSingleFileAsync(
                    singleFile,
                    new SingleFileRunOptions(configuration, noBuild, noRestore, properties, isJson),
                    cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, isJson);
            }

            if (outcome.Resolution is null)
            {
                var code = outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, $"Build failed (exit code {code}).");
                }
                return code;
            }

            var resolution = outcome.Resolution;
            var outputFolder = new DirectoryInfo(resolution.OutputDirectory);

            FileInfo resolvedManifest;
            try
            {
                resolvedManifest = await ResolveSingleFileManifestAsync(resolution, manifest, outputFolder, isJson, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, isJson);
            }

            WarnOnSingleFileIdentityCollision(resolvedManifest, outputAppXDirectory ?? new DirectoryInfo(Path.Combine(outputFolder.FullName, "AppX")), isJson);

            // A tier-3 manifest can be named <stem>.appxmanifest, which ManifestHelper.FindManifest does not
            // probe for, so the resolved manifest is always passed explicitly rather than left to
            // folder-mode auto-detection.
            // Pass the .cs itself as the "project file": AddLooseLayoutIdentityAsync resolves the app's
            // NuGet package list from it (via `dotnet package list --file`) to decide the Windows App SDK
            // framework dependency and runtime. Passing null instead would fall back to globbing the
            // current directory for any .csproj, which for a file-based app can only find an unrelated
            // project — and would write THAT project's Windows App SDK dependency into this app's manifest.
            return await ExecuteRunPipelineAsync(
                outputFolder, resolvedManifest, outputAppXDirectory, appArgs,
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, isJson,
                runtimeArch: resolution.Architecture,
                projectFile: singleFile,
                framework: resolution.TargetFramework,
                noRestore: noRestore,
                cancellationToken);
        }

        /// <summary>
        /// Resolves the manifest for a file-based app, in strict precedence order:
        /// <list type="number">
        ///   <item><c>--manifest</c> — an explicit path on the command line always wins.</item>
        ///   <item><c>WinAppManifestPath</c> — the same escape hatch the NuGet targets expose, declared from the <c>.cs</c>.</item>
        ///   <item>A manifest the user authored <b>next to the <c>.cs</c></b>, named <c>&lt;stem&gt;.appxmanifest</c> — used verbatim; nothing is generated.</item>
        ///   <item>Otherwise, generate <c>Package.appxmanifest</c> into the build output.</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This deliberately inverts the probe order used by the
        /// <c>Microsoft.Windows.SDK.BuildTools.WinApp</c> targets</b>, which check the OUTPUT directory
        /// before the project directory. That order is correct for MAUI, where the manifest is a build
        /// product generated into <c>$(OutputPath)</c>. It would be actively wrong here: winapp generates
        /// into the output directory, so under that order the generated file would permanently shadow a
        /// manifest the user hand-wrote next to their <c>.cs</c> and their edits would silently never apply.
        /// </para>
        /// <para>
        /// Tier 4 regenerates on every run. The manifest is a build product in a temp directory, and the
        /// <c>#:property</c> values it is derived from can change between runs.
        /// </para>
        /// <para>
        /// The names differ by tier on purpose. The SOURCE directory is shared — <c>foo.cs</c> and
        /// <c>bar.cs</c> can sit side by side — so tier 3 discovers ONLY the per-file
        /// <c>&lt;stem&gt;.appxmanifest</c>, matching the SDK's own per-file <c>&lt;stem&gt;.run.json</c>
        /// convention. The OUTPUT directory is not shared: the SDK gives every file-based app its own
        /// <c>%TEMP%\dotnet\runfile\&lt;stem&gt;-&lt;hash&gt;\</c>, so tier 4 writes the canonical
        /// <c>Package.appxmanifest</c> that every existing winapp probe already understands.
        /// </para>
        /// </remarks>
        private async Task<FileInfo> ResolveSingleFileManifestAsync(
            SingleFileRunResolution resolution,
            FileInfo? explicitManifest,
            DirectoryInfo outputFolder,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // Tier 1: --manifest. Left for ExecuteRunPipelineAsync to validate and report.
            if (explicitManifest != null)
            {
                return explicitManifest;
            }

            var stem = Path.GetFileNameWithoutExtension(resolution.SingleFile.Name);

            // Tier 2: an explicit WinAppManifestPath declared by the app itself.
            if (resolution.Properties.TryGetValue("WinAppManifestPath", out var declaredPath) &&
                !string.IsNullOrWhiteSpace(declaredPath))
            {
                var declared = new FileInfo(Path.GetFullPath(declaredPath.Trim(), resolution.SingleFile.DirectoryName ?? "."));
                if (!declared.Exists)
                {
                    throw new ProjectRunException(
                        $"'{resolution.SingleFile.Name}' sets WinAppManifestPath to '{declared.FullName}', but no file exists there. " +
                        "Point it at an existing manifest, or remove it to let winapp generate one.");
                }

                LogSingleFileManifestSource(declared, "declared by WinAppManifestPath", isJson);
                return declared;
            }

            // Tier 3: a manifest the user authored next to the .cs.
            var sourceDirectory = resolution.SingleFile.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
            var authored = FindAuthoredSingleFileManifest(sourceDirectory, stem);
            if (authored != null)
            {
                LogSingleFileManifestSource(authored, "found next to the file", isJson);
                return authored;
            }

            // Tier 4: generate into the build output. The output directory belongs to exactly one .cs, so
            // the canonical Package.appxmanifest name is unambiguous there and stays discoverable by every
            // existing winapp manifest probe.
            var info = SingleFileManifestPlanner.Plan(resolution.SingleFile, resolution.Properties);
            const string manifestFileName = "Package.appxmanifest";

            // The status task converts a thrown exception into a non-zero return code rather than
            // rethrowing, so ignoring the result would let a failed generation (disk full, denied write)
            // fall through to registration against a missing or half-written manifest.
            var generateResult = await statusService.ExecuteWithStatusAsync("Generating manifest...", async (taskContext, ct) =>
            {
                await GenerateSingleFileManifestAsync(outputFolder, info, resolution.ExecutableName, manifestFileName, taskContext, ct);
                return (0, $"Manifest generated for {info.PackageName} {info.Version}");
            }, cancellationToken);

            if (generateResult != 0)
            {
                throw new ProjectRunException(
                    $"Could not generate a manifest for '{resolution.SingleFile.Name}' in '{outputFolder.FullName}'.");
            }

            return new FileInfo(Path.Combine(outputFolder.FullName, manifestFileName));
        }

        /// <summary>
        /// Writes the inferred manifest and the default asset set into the build output.
        /// </summary>
        /// <remarks>
        /// <paramref name="executableName"/> is written CONCRETELY (e.g. <c>Executable="counter.exe"</c>)
        /// rather than as the <c>$targetnametoken$.exe</c> build placeholder. Every WinAppSDK
        /// self-contained output ships a <c>RestartAgent.exe</c> next to the app exe, so the placeholder
        /// form would always trip the "multiple .exe files found" ambiguity and force the user to pass
        /// <c>--executable</c> on every run.
        /// </remarks>
        private Task GenerateSingleFileManifestAsync(
            DirectoryInfo outputFolder,
            SingleFileManifestInfo info,
            string executableName,
            string manifestFileName,
            TaskContext taskContext,
            CancellationToken cancellationToken) =>
            manifestTemplateService.GenerateCompleteManifestAsync(
                outputFolder,
                info.PackageName,
                info.PublisherDN,
                info.Version,
                ManifestTemplates.Packaged,
                info.Description,
                taskContext,
                manifestFileName,
                executableName,
                info.DisplayName,
                SingleFileManifestPlanner.ApplicationId,
                cancellationToken);

        /// <summary>
        /// Finds a manifest the user authored next to the <c>.cs</c>.
        /// </summary>
        /// <remarks>
        /// Only the per-file <c>&lt;stem&gt;.appxmanifest</c> name is discovered implicitly. The
        /// directory-wide names (<c>Package.appxmanifest</c>, <c>appxmanifest.xml</c>) are deliberately
        /// NOT probed: file-based apps are per-file and can sit side by side, so a shared
        /// <c>Package.appxmanifest</c> would silently be applied to every <c>.cs</c> in the folder —
        /// registering <c>bar.cs</c> under <c>foo.cs</c>'s identity. A user who genuinely wants one
        /// manifest for several files points at it explicitly with <c>--manifest</c> or
        /// <c>#:property WinAppManifestPath</c>.
        /// </remarks>
        private static FileInfo? FindAuthoredSingleFileManifest(DirectoryInfo sourceDirectory, string stem)
        {
            var path = Path.Combine(sourceDirectory.FullName, $"{stem}.appxmanifest");
            return File.Exists(path) ? new FileInfo(path) : null;
        }

        private void LogSingleFileManifestSource(FileInfo manifest, string reason, bool isJson)
        {
            if (!isJson && logger.IsEnabled(LogLevel.Debug))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Note} Using manifest {manifest.FullName} ({reason}).");
            }
        }

        /// <summary>
        /// Warns when registering will REPLACE an existing development registration of the same package
        /// name that was installed from a different location.
        /// </summary>
        /// <remarks>
        /// Two <c>counter.cs</c> files in different directories both default to
        /// <c>Identity Name="counter"</c>, so the second run silently replaces the first. Suffixing the
        /// identity with a path hash would prevent that, but it would make the AUMID and Start-menu entry
        /// ugly for everyone in order to protect a rare case — and identity is already scoped by
        /// <c>Publisher=CN=&lt;user&gt;</c>, so it can never collide across users. Keep the clean default
        /// and make the replacement visible instead of silent.
        /// <para>
        /// <paramref name="layoutDirectory"/> must be the EFFECTIVE AppX layout directory (honoring
        /// <c>--output-appx-directory</c>), because that is what a registration's install location points
        /// at. Comparing against the build output instead would warn about every re-run that uses a
        /// custom layout path, which trains users to ignore a warning that is usually real.
        /// </para>
        /// </remarks>
        private void WarnOnSingleFileIdentityCollision(FileInfo manifest, DirectoryInfo layoutDirectory, bool isJson)
        {
            // Gate on Warning, not Information: --quiet suppresses Information but still promises
            // warnings, and silently replacing another app's registration is exactly what a user needs
            // to hear about.
            if (isJson || !logger.IsEnabled(LogLevel.Warning))
            {
                return;
            }

            try
            {
                if (!manifest.Exists)
                {
                    return;
                }

                var document = AppxManifestDocument.Load(manifest.FullName);
                var packageName = document.IdentityName;
                if (string.IsNullOrEmpty(packageName))
                {
                    return;
                }

                foreach (var existing in packageRegistrationService.FindDevPackages(packageName))
                {
                    if (!existing.IsDevelopmentMode || string.IsNullOrEmpty(existing.InstallLocation))
                    {
                        continue;
                    }

                    if (PathsPointToSameLocation(existing.InstallLocation, layoutDirectory.FullName))
                    {
                        continue;
                    }

                    logger.LogWarning(
                        "{UISymbol} Replacing the existing registration of '{PackageName}', which was installed from a different location ({InstallLocation}). Set '#:property {Property}=<name>' to give this app its own package identity.",
                        UiSymbols.Warning, packageName, existing.InstallLocation, SingleFileManifestPlanner.PackageNameProperty);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Purely advisory — never let the check itself fail the run.
                logger.LogDebug("Could not check for an existing registration: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// True when two paths refer to the same directory, or when one contains the other. A registered
        /// package's install location is the AppX layout NESTED inside the build output, so a plain equality
        /// check would report every re-run as a cross-location collision.
        /// </summary>
        private static bool PathsPointToSameLocation(string first, string second)
        {
            static string Normalize(string path) =>
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            try
            {
                var a = Normalize(first);
                var b = Normalize(second);
                return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                    || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}

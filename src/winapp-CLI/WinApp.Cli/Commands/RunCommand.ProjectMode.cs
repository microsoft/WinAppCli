// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Logs a user-facing error, emits the error-shaped JSON envelope in <c>--json</c> mode, and
        /// returns exit code 1. Consolidates the repeated log + PrintJson + return pattern (spec L5).
        /// </summary>
        private int Fail(string message, bool isJson)
        {
            logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
            if (isJson)
            {
                PrintJson(aumid: null, processId: null, message);
            }
            return 1;
        }

        /// <summary>
        /// Collects the launch/identity options that are only valid for a packaged (MSIX) app and so
        /// must be rejected for an unpackaged one. Shared by the pre-build fast-fail in
        /// <see cref="RunProjectModeAsync"/> and the authoritative post-build gate in
        /// <see cref="RunUnpackagedProjectAsync"/> so both reject the exact same set (issue #676).
        /// </summary>
        private static List<string> CollectUnpackagedIncompatibleOptions(
            bool noLaunch, bool withAlias, bool unregisterOnExit, bool clean, FileInfo? manifest, DirectoryInfo? outputAppXDirectory, string? executable)
        {
            var rejected = new List<string>();
            if (noLaunch)
            {
                rejected.Add("--no-launch");
            }
            if (withAlias)
            {
                rejected.Add("--with-alias");
            }
            if (unregisterOnExit)
            {
                rejected.Add("--unregister-on-exit");
            }
            if (clean)
            {
                rejected.Add("--clean");
            }
            if (manifest != null)
            {
                rejected.Add("--manifest");
            }
            if (outputAppXDirectory != null)
            {
                rejected.Add("--output-appx-directory");
            }
            if (!string.IsNullOrWhiteSpace(executable))
            {
                // --executable selects an entry within an MSIX layout; an unpackaged app is launched
                // from the project's own build output (RunCommand), so honoring it is impossible — reject
                // rather than silently ignore.
                rejected.Add("--executable");
            }
            return rejected;
        }

        /// <summary>Builds the user-facing message listing the packaged-only options rejected for an unpackaged app.</summary>
        private static string BuildUnpackagedIncompatibleMessage(IReadOnlyList<string> rejected, string csprojName)
            => $"The option(s) {string.Join(", ", rejected)} don't apply to unpackaged apps — they're only valid for packaged (MSIX) apps. " +
               $"'{csprojName}' resolves to an unpackaged WinUI app (WindowsPackageType=None). Remove them, or make the app packaged to use them.";

        /// <summary>
        /// Project-mode entry point (spec §7/§8): build the <c>.csproj</c>, resolve its MSBuild
        /// output properties, then launch it as packaged (loose-layout register + AUMID, reusing the
        /// shared folder pipeline) or unpackaged (launch the apphost <c>.exe</c> directly). Folder
        /// mode never reaches here.
        /// </summary>
        private async Task<int> RunProjectModeAsync(
            ParseResult parseResult,
            FileInfo csproj,
            FileInfo? solution,
            string? appArgs,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // Project-mode build inputs.
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
            var framework = parseResult.GetValue(FrameworkOption);
            var noBuild = parseResult.GetValue(NoBuildOption);
            var noRestore = parseResult.GetValue(NoRestoreOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];

            // Reject malformed -p values early (spec L3): each must be Name=Value with a non-empty,
            // non-whitespace name, otherwise it would become a nonsensical '-p:=Value' / '-p: =Value'
            // MSBuild argument.
            foreach (var property in properties)
            {
                // MSBuild's /p splits a single token on ';' into MULTIPLE properties, so a raw ';' here
                // packs more than one property into one -p. That smuggles a dedicated-flag property (e.g.
                // RuntimeIdentifier=win-arm64) past the ForwardableProperties filter — which only inspects
                // the name before the FIRST '=' — and lets it override the arch winapp conveys via the RID.
                // The option is repeatable, so reject packing and point at the escape for a literal ';'.
                if (property.Contains(';'))
                {
                    return Fail(
                        $"Invalid --property '{property}'. A single -p cannot pack multiple properties with ';'. " +
                        "Pass one property per repeatable -p (for example: -p A=1 -p B=2), or escape a literal ';' in a value as '%3B'.",
                        isJson);
                }

                var separator = property.IndexOf('=');
                if (separator <= 0 || string.IsNullOrWhiteSpace(property[..separator]))
                {
                    return Fail($"Invalid --property '{property}'. Expected Name=Value (for example: -p WindowsPackageType=None).", isJson);
                }
            }

            // Shared launch/identity options (validity depends on packaging, checked below).
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

            // Resolve the target architecture: --runtime's arch beats --arch; else the process arch.
            if (!TryResolveArchitecture(archOption, runtimeOption, out var architecture, out var archError))
            {
                return Fail(archError!, isJson);
            }

            // A capable SDK (≥ 8.0.100) is required for MSBuild --getProperty.
            var workingDir = csproj.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
            var sdkError = await projectRunService.CheckSdkAsync(workingDir, cancellationToken);
            if (sdkError != null)
            {
                return Fail(sdkError, isJson);
            }

            // Build (unless --no-build) and resolve the output properties. ProjectRunService owns the
            // build UX (Change #1/#4): it streams dotnet output live, shows an interactive spinner for
            // humans, and maps --verbose to dotnet's -v. In --json mode it suppresses the banner and
            // routes build output to stderr to keep stdout pure JSON.
            var buildOptions = new ProjectRunOptions(configuration, architecture, framework, noBuild, noRestore, properties, isJson, solution);

            // Fail fast (issue #676): identity-only options like --no-launch are meaningless for an
            // unpackaged app, but are only rejected authoritatively AFTER packaging is known (post-build,
            // in RunUnpackagedProjectAsync). Rather than making the user pay the full build cost only to
            // be rejected, cheaply evaluate WindowsPackageType first and reject now when the project is
            // DEFINITIVELY unpackaged (explicit WindowsPackageType=None). Packaged/indeterminate projects
            // fall through to the build + authoritative gate. Skipped under --no-build (no build cost to
            // save; that path's evaluate + gate are already fast).
            if (!noBuild)
            {
                var incompatible = CollectUnpackagedIncompatibleOptions(noLaunch, withAlias, unregisterOnExit, clean, manifest, outputAppXDirectory, executable);
                if (incompatible.Count > 0
                    && await projectRunService.IsDefinitivelyUnpackagedAsync(csproj, buildOptions, cancellationToken))
                {
                    return Fail(BuildUnpackagedIncompatibleMessage(incompatible, csproj.Name), isJson);
                }
            }

            ProjectBuildOutcome outcome;
            try
            {
                outcome = await projectRunService.BuildAndResolveAsync(csproj, buildOptions, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, isJson);
            }

            if (outcome.Resolution is null)
            {
                // Build failed — dotnet already surfaced its diagnostics. Propagate its exit code.
                var code = outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, $"Build failed (exit code {code}).");
                }
                return code;
            }

            var resolution = outcome.Resolution;

            return resolution.Packaging == ProjectPackaging.Packaged
                ? await RunPackagedProjectAsync(
                    resolution, csproj, manifest, outputAppXDirectory, appArgs,
                    noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, isJson,
                    cancellationToken)
                : await RunUnpackagedProjectAsync(
                    resolution, csproj, appArgs,
                    noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, manifest, outputAppXDirectory, isJson,
                    cancellationToken);
        }

        /// <summary>
        /// Packaged project-mode launch (spec §7.2): point the shared folder pipeline at the build's
        /// TargetDir (which contains the MSBuild-generated <c>AppxManifest.xml</c> + recipe) and pass
        /// the resolved arch + project file so the correct-arch Windows App Runtime is installed.
        /// </summary>
        private async Task<int> RunPackagedProjectAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            bool isJson,
            CancellationToken cancellationToken)
        {
            var targetDir = new DirectoryInfo(resolution.TargetDir);

            // Guardrail: packaged (per the evaluated WindowsPackageType) but no manifest in the
            // build output is a misconfiguration — surface it clearly instead of the generic
            // "manifest not found" from the shared pipeline (which would also probe the cwd).
            if (manifest == null && !FindManifest(targetDir.FullName).Exists)
            {
                var message =
                    $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the build output ({targetDir.FullName}). " +
                    "Ensure the project is a packaged WinUI app (EnableMsixTooling=true with a Package.appxmanifest), or force an unpackaged run with -p:WindowsPackageType=None.";
                return Fail(message, isJson);
            }

            return await ExecuteRunPipelineAsync(
                targetDir, manifest, outputAppXDirectory, appArgs,
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, isJson,
                runtimeArch: resolution.Architecture, projectFile: csproj, framework: resolution.Framework, cancellationToken);
        }

        /// <summary>
        /// Unpackaged project-mode launch (spec §7.3): ensure the (framework-dependent) Windows App
        /// Runtime is installed for the app's arch, then launch the apphost <c>.exe</c> directly.
        /// Identity-only options are rejected since there is no MSIX package.
        /// </summary>
        private async Task<int> RunUnpackagedProjectAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // Reject options that only make sense for a packaged (MSIX identity) app. This is the
            // AUTHORITATIVE gate — it runs once packaging is definitively known. RunProjectModeAsync
            // additionally fails fast on the definitively-unpackaged case before building (issue #676),
            // but this gate still catches the indeterminate-then-unpackaged case that only resolves here.
            var rejected = CollectUnpackagedIncompatibleOptions(noLaunch, withAlias, unregisterOnExit, clean, manifest, outputAppXDirectory, executable);
            if (rejected.Count > 0)
            {
                return Fail(BuildUnpackagedIncompatibleMessage(rejected, csproj.Name), isJson);
            }

            var exePath = resolution.RunCommand!; // guaranteed non-null for unpackaged by BuildAndResolveAsync
            var workingDirectory = Path.GetDirectoryName(exePath);

            // Install the framework-dependent Windows App Runtime (Framework + DDLM) the app's
            // bootstrapper needs, for the resolved arch. Self-contained apps carry their own copy.
            if (!resolution.SelfContained)
            {
                var runtimeResult = await statusService.ExecuteWithStatusAsync(
                    "Preparing Windows App Runtime...",
                    async (taskContext, ct) =>
                    {
                        try
                        {
                            await msixService.EnsureWindowsAppRuntimeInstalledAsync(csproj, resolution.Architecture, resolution.Framework, taskContext, ct);
                            return (0, "Windows App Runtime ready");
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Honor Ctrl+C instead of translating it into a runtime-prep failure message.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            return (1, $"{UiSymbols.Error} Failed to prepare the Windows App Runtime: {ex.Message}");
                        }
                    },
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    // Ctrl+C during runtime prep — exit as cancelled (matching the launched-process wait)
                    // rather than reporting a genuine "Failed to prepare the Windows App Runtime" error.
                    return -1;
                }

                if (runtimeResult != 0)
                {
                    if (isJson)
                    {
                        PrintJson(aumid: null, processId: null, "Failed to prepare the Windows App Runtime.");
                    }
                    return runtimeResult;
                }
            }

            ILaunchedProcess launched;
            try
            {
                // For --detach and --json the child must not inherit winapp's standard handles: inheritance
                // would keep the npm wrapper's captured stdout pipe open (blocking a detached launch) and let
                // app output corrupt --json stdout. A foreground, non-JSON run streams inline like `dotnet run`.
                var stdioMode = (detach || isJson) ? LaunchStdioMode.Suppress : LaunchStdioMode.Inherit;
                launched = appLauncherService.LaunchExecutable(exePath, appArgs, workingDirectory, stdioMode);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Failed to launch '{Exe}': {Message}", UiSymbols.Error, exePath, ex.Message);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, ex.Message);
                }
                return 1;
            }

            // Own the handle for the lifetime of the wait so the exit code survives the process
            // exiting and the PID can't be reused out from under us. Disposing does not kill the OS
            // process, so the --detach path below can return while the app keeps running.
            using (launched)
            {
                var processId = launched.ProcessId;

                // --detach: return immediately, surfacing the PID for automation.
                if (detach)
                {
                    if (isJson)
                    {
                        PrintJson(aumid: null, processId, errorMessage: null);
                    }
                    else
                    {
                        ansiConsole.WriteLine(processId.ToString());
                    }
                    return 0;
                }

                if (isJson)
                {
                    PrintJson(aumid: null, processId, errorMessage: null);
                }

                // --debug-output: attach the debug event loop instead of a plain wait.
                if (debugOutput)
                {
                    var debugExit = await debugOutputService.RunDebugLoopAsync(processId, cancellationToken, useSymbols,
                        symbolSearchPaths: [resolution.TargetDir]);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        launched.Kill();
                    }
                    return debugExit;
                }

                return await WaitForLaunchedProcessAsync(launched, cancellationToken);
            }
        }

        /// <summary>
        /// Waits for a directly-launched (unpackaged) process to exit and returns its exit code,
        /// mirroring the folder-mode wait semantics (already-exited returns the real code and Ctrl+C
        /// kills the child). The owned handle keeps the exit code valid even if the process exited
        /// before the wait began, so we never misreport a crash as success.
        /// </summary>
        private static async Task<int> WaitForLaunchedProcessAsync(ILaunchedProcess launched, CancellationToken cancellationToken)
        {
            try
            {
                await launched.WaitForExitAsync(cancellationToken);
                return launched.ExitCode;
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C — kill the launched process before exiting.
                launched.Kill();
                return -1;
            }
        }

        /// <summary>
        /// Computes the effective build inputs threaded into candidate classification so a multi-
        /// <c>.csproj</c> directory or solution is resolved under the SAME Configuration/arch/TFM/user
        /// <c>-p</c> the build will use (not MSBuild defaults). Returns null when the architecture can't
        /// be resolved — classification then uses defaults and <see cref="RunProjectModeAsync"/> surfaces
        /// the arch error. Reads only project-mode options and is a no-op for folder mode (which never
        /// classifies), so evaluating it before mode is known is harmless.
        /// </summary>
        internal static ProjectClassificationInputs? BuildClassificationInputs(ParseResult parseResult)
        {
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
            if (!TryResolveArchitecture(archOption, runtimeOption, out var architecture, out _))
            {
                return null;
            }

            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var framework = parseResult.GetValue(FrameworkOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];
            return new ProjectClassificationInputs(configuration, architecture, framework, properties);
        }

        /// <summary>
        /// Resolves the canonical target architecture from <c>--arch</c> / <c>--runtime</c>.
        /// <c>--runtime</c>'s architecture wins over <c>--arch</c> (mirrors dotnet, where a RID is
        /// more specific); when neither is given, the current process architecture is used.
        /// </summary>
        internal static bool TryResolveArchitecture(string? archOption, string? runtimeOption, out string architecture, out string? error)
        {
            error = null;
            architecture = string.Empty;

            string? fromRuntime = null;
            if (!string.IsNullOrWhiteSpace(runtimeOption))
            {
                fromRuntime = RunArchHelper.ArchitectureFromRid(runtimeOption);
                if (fromRuntime == null)
                {
                    error = $"Could not determine an architecture from --runtime '{runtimeOption}'. Use a RID such as win-x64, win-arm64, or win-x86.";
                    return false;
                }
            }

            string? fromArch = null;
            if (!string.IsNullOrWhiteSpace(archOption))
            {
                fromArch = RunArchHelper.NormalizeArchitecture(archOption);
                if (fromArch == null)
                {
                    error = $"Unsupported --arch '{archOption}'. Supported values: {string.Join(", ", RunArchHelper.SupportedArchitectures)}.";
                    return false;
                }
            }

            architecture = fromRuntime ?? fromArch ?? RunArchHelper.DefaultArchitecture();
            return true;
        }
    }
}

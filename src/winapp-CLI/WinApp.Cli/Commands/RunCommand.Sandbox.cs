// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Runs a packaged app in the execution target: materialize on the host, reconcile into the
        /// guest, then let guest winapp register, launch, and honour the option matrix.
        /// </summary>
        /// <remarks>
        /// Nothing about this machine changes. Materialization stops before runtime provisioning and
        /// registration, and the guest performs both — so a <c>--sandbox</c> run leaves no package
        /// registered here and installs no runtime here, which is the entire point of the flag.
        /// <para>
        /// The guest is asked to perform the ordinary <c>winapp run</c>. Every option in the matrix
        /// is therefore the same implementation users already rely on locally rather than a second
        /// one that can drift.
        /// </para>
        /// </remarks>
        private async Task<int> ExecutePackagedSandboxRunAsync(
            DirectoryInfo inputFolder,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            string? executable,
            bool isJson,
            FileInfo? projectFile,
            string? framework,
            bool noRestore,
            CancellationToken cancellationToken)
        {
            FileInfo resolvedManifest;
            DirectoryInfo layout;
            MsixIdentityResult? identity = null;

            try
            {
                resolvedManifest = ResolveManifestForSandbox(inputFolder, manifest);
                layout = outputAppXDirectory ?? new DirectoryInfo(Path.Combine(inputFolder.FullName, "AppX"));

                LongPathHelper.ValidatePathLength(resolvedManifest.FullName);
                LongPathHelper.ValidatePathLength(layout.FullName);

                var materializeError = (string?)null;
                var materialized = await statusService.ExecuteWithStatusAsync(
                    "Preparing application layout...",
                    async (taskContext, ct) =>
                    {
                        try
                        {
                            identity = await msixService.MaterializeLooseLayoutAsync(
                                resolvedManifest, inputFolder, layout, taskContext,
                                executable, projectFile, framework, noRestore, ct);
                            return (0, $"{identity.PackageName} ready to deploy");
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            materializeError = ex.Message;
                            return (1, $"{UiSymbols.Error} Failed to prepare the application: {ex.Message}");
                        }
                    },
                    cancellationToken);

                if (materialized != 0 || identity is null)
                {
                    return Fail(materializeError ?? "Failed to prepare the application.", isJson);
                }
            }
            catch (OperationCanceledException)
            {
                return -1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException)
            {
                return Fail(ex.Message, isJson);
            }

            var options = new GuestRunOptions(
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, isJson, appArgs);

            return await RunInGuestAsync(
                layout,
                DeploymentIdFor(inputFolder, identity),
                clean,
                isJson,
                requiresRealInput: !noLaunch,
                identity,
                (deployment, ownerEnvironment) => new GuestExecRequest
                {
                    UseGuestWinapp = true,
                    Arguments = GuestRunPlanner.BuildRunArguments(
                        deployment.PayloadPath, deployment.LayoutPath, options),

                    // The payload folder, so a guest app that resolves files relative to its working
                    // directory sees its own deployment rather than the agent's location.
                    WorkingDirectory = deployment.PayloadPath,
                    Environment = ownerEnvironment,
                    RequiresRealInput = !noLaunch,
                },
                cancellationToken);
        }

        /// <summary>
        /// Runs an unpackaged app in the execution target by starting its apphost there directly.
        /// </summary>
        /// <remarks>
        /// There is no package to register, so this deploys the build output and starts the
        /// executable — the guest analogue of what an unpackaged local run does. The host's working
        /// directory is deliberately not reproduced: it names a location that does not exist in the
        /// guest, so the deployment folder is used and documented instead of silently substituting a
        /// path the app was never pointed at.
        /// </remarks>
        private async Task<int> ExecuteUnpackagedSandboxRunAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            string? appArgs,
            bool debugOutput,
            bool detach,
            bool isJson,
            CancellationToken cancellationToken)
        {
            var targetDir = new DirectoryInfo(resolution.TargetDir);

            string executableRelativePath;

            try
            {
                GuestRunPlanner.EnsureSupportedForUnpackaged(new GuestRunOptions(DebugOutput: debugOutput));
                executableRelativePath = ResolveGuestRelativeExecutable(targetDir, resolution.RunCommand!, csproj);
            }
            catch (ExecutionTargetException ex)
            {
                return SandboxOutput.Fail(ansiConsole, isJson, ex.Error);
            }

            // A non-apphost RunCommand (for example dotnet) carries leading arguments that must
            // precede the user's, exactly as they do locally.
            var launchArguments = WindowsCommandLine.SplitArguments(
                CombineLaunchArguments(resolution.RunArguments, appArgs) ?? string.Empty);

            return await RunInGuestAsync(
                targetDir,
                DeploymentPlanner.CreateDeploymentId(Path.GetFullPath(targetDir.FullName), originalPackageIdentity: null),
                clean: false,
                isJson,
                requiresRealInput: true,
                identity: null,
                (deployment, ownerEnvironment) => new GuestExecRequest
                {
                    Executable = TargetPathSafety.CombineInsideRoot(deployment.PayloadPath, executableRelativePath),
                    Arguments = [.. launchArguments],
                    WorkingDirectory = deployment.PayloadPath,
                    Environment = ownerEnvironment,
                    RequiresRealInput = true,
                },
                cancellationToken,
                detach);
        }

        /// <summary>
        /// The shared half of every <c>run --sandbox</c>: prepare, deploy, run, relay.
        /// </summary>
        /// <param name="sourceRoot">Host folder to reconcile into the guest.</param>
        /// <param name="deploymentId">Internal deployment identity.</param>
        /// <param name="clean">Whether to discard the guest copy first.</param>
        /// <param name="isJson">Whether the invoking command is in machine-readable mode.</param>
        /// <param name="requiresRealInput">Whether the guest command needs a usable input desktop.</param>
        /// <param name="identity">Package identity to record ownership for, when there is one.</param>
        /// <param name="buildRequest">Builds the guest request once the guest paths are known.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <param name="detachAfterStart">
        /// True to return as soon as the guest reports the process started, for an unpackaged
        /// <c>--detach</c>. The packaged path forwards <c>--detach</c> to guest winapp instead, so
        /// that flag keeps its exact local meaning.
        /// </param>
        private async Task<int> RunInGuestAsync(
            DirectoryInfo sourceRoot,
            string deploymentId,
            bool clean,
            bool isJson,
            bool requiresRealInput,
            MsixIdentityResult? identity,
            Func<GuestDeployment, Dictionary<string, string>, GuestExecRequest> buildRequest,
            CancellationToken cancellationToken,
            bool detachAfterStart = false)
        {
            try
            {
                await using var target = await executionTargetOrchestrator.PrepareAsync(
                    PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = requiresRealInput },
                    cancellationToken);

                WriteProgress(isJson, ExecutionTargetOrchestrator.DescribeProgress(target.Reused));

                var deployment = await guestApplicationRunner.DeployAsync(
                    target, deploymentId, sourceRoot, clean, cancellationToken);

                var state = deployment.State;

                if (identity is not null)
                {
                    var familyName = appLauncherService.ComputePackageFamilyName(identity.PackageName, identity.Publisher);

                    state = guestApplicationRunner.CommitPackage(state, new PackageOwnership
                    {
                        PackageName = identity.PackageName,
                        Publisher = identity.Publisher,
                        PackageFamilyName = familyName,
                        RegisteredLocation = deployment.LayoutPath,
                        Aumid = $"{familyName}!{identity.ApplicationId}",
                    });
                }

                var ownerEnvironment = GuestOwnerContext.WithOwner(
                    environment: null,
                    GuestOwnerContext.ResolveGuestToken(
                        ExecutionTargetRef.WindowsSandboxDefault.Id, target.Epoch.Value));

                using var detachSignal = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, detachSignal.Token);

                var startedProcessId = 0;

                // Under --json the guest's payload is captured rather than relayed, so the additive
                // execution-target members can be merged into the document the caller parses. It is
                // never reformatted blind: anything that does not parse is written through exactly
                // as the guest produced it, because corrupting a result is worse than omitting a
                // field.
                var capturedOutput = isJson ? new MemoryStream() : null;

                var exitCode = await guestApplicationRunner.RunAsync(
                    target,
                    state,
                    buildRequest(deployment, ownerEnvironment),
                    new GuestExecCallbacks(
                        OnStarted: process =>
                        {
                            startedProcessId = process.ProcessId;

                            if (detachAfterStart)
                            {
                                detachSignal.Cancel();
                            }
                        },
                        OnStandardOutput: data =>
                        {
                            if (capturedOutput is not null)
                            {
                                CaptureBounded(capturedOutput, data);
                                return;
                            }

                            WriteRawToConsole(Console.OpenStandardOutput(), data);
                        },
                        OnStandardError: data => WriteRawToConsole(Console.OpenStandardError(), data)),
                    linked.Token);

                if (capturedOutput is not null)
                {
                    PublishGuestJson(capturedOutput, target, startedProcessId);
                }

                return exitCode;
            }
            catch (OperationCanceledException) when (detachAfterStart && !cancellationToken.IsCancellationRequested)
            {
                // Detach for an unpackaged app: the guest process is running and stays running; the
                // channel closing does not stop it, because the agent owns it, not this connection.
                return 0;
            }
            catch (ExecutionTargetException ex)
            {
                return SandboxOutput.Fail(ansiConsole, isJson, ex.Error);
            }
        }

        /// <summary>
        /// The deployment identity for a resolved input, derived from its canonical path and
        /// original package identity so two projects sharing an identity stay distinct.
        /// </summary>
        private static string DeploymentIdFor(DirectoryInfo inputFolder, MsixIdentityResult identity) =>
            DeploymentPlanner.CreateDeploymentId(
                Path.GetFullPath(inputFolder.FullName),
                $"{identity.PackageName}_{identity.Publisher}");

        /// <summary>Resolves the manifest for a sandbox run using the same precedence as a local one.</summary>
        private FileInfo ResolveManifestForSandbox(DirectoryInfo inputFolder, FileInfo? manifest)
        {
            if (manifest is not null)
            {
                if (!manifest.Exists)
                {
                    throw new FileNotFoundException($"Manifest file not found: {manifest.FullName}");
                }

                return manifest;
            }

            var folderManifest = FindManifest(inputFolder.FullName);
            if (folderManifest.Exists)
            {
                return folderManifest;
            }

            var cwdManifest = FindManifest(currentDirectoryProvider.GetCurrentDirectory());
            if (cwdManifest.Exists)
            {
                return cwdManifest;
            }

            throw new FileNotFoundException(
                $"Manifest file not found. Searched in: input folder ({inputFolder.FullName}), current directory ({currentDirectoryProvider.GetCurrentDirectory()}). Use --manifest to specify the path.");
        }

        /// <summary>
        /// Expresses the resolved apphost as a path relative to the deployed folder.
        /// </summary>
        /// <remarks>
        /// The host's absolute path means nothing in the guest, and an executable outside the folder
        /// being deployed would simply not be there — so that is refused rather than turned into a
        /// launch failure the user cannot interpret.
        /// </remarks>
        private static string ResolveGuestRelativeExecutable(DirectoryInfo targetDir, string executablePath, FileInfo csproj)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir.FullName));
            var full = Path.GetFullPath(executablePath);

            if (!TargetPathSafety.IsInsideRoot(root, full))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.Unsupported,
                    $"'{csproj.Name}' launches an executable outside its build output, which cannot be deployed into Windows Sandbox.",
                    userAction: "Publish the app as self-contained, or run it without --sandbox.",
                    context: new Dictionary<string, string> { ["executable"] = Path.GetFileName(full) });
            }

            return full[(root.Length + 1)..];
        }

        /// <summary>Writes a progress line without disturbing a machine-readable stdout.</summary>
        private void WriteProgress(bool isJson, string message)
        {
            if (isJson)
            {
                Console.Error.WriteLine(message);
                return;
            }

            ansiConsole.MarkupLineInterpolated($"{message}");
        }

        /// <summary>Largest guest JSON payload captured for augmentation before it is relayed as-is.</summary>
        /// <remarks>
        /// A run result is a few hundred bytes. The bound exists so a guest that produced something
        /// unexpected on a machine-readable stream cannot make the host buffer without limit.
        /// </remarks>
        internal const int MaxCapturedJsonBytes = 1024 * 1024;

        /// <summary>Captures guest output up to the bound, discarding the excess.</summary>
        private static void CaptureBounded(MemoryStream buffer, ReadOnlyMemory<byte> data)
        {
            var remaining = MaxCapturedJsonBytes - (int)buffer.Length;
            if (remaining <= 0)
            {
                return;
            }

            buffer.Write(data.Span[..Math.Min(remaining, data.Length)]);
        }

        /// <summary>
        /// Emits the guest's machine-readable result with the execution-target members merged in.
        /// </summary>
        internal void PublishGuestJson(MemoryStream captured, PreparedTarget target, int processId)
        {
            ArgumentNullException.ThrowIfNull(captured);
            ArgumentNullException.ThrowIfNull(target);

            var bytes = captured.ToArray();
            if (bytes.Length == 0)
            {
                return;
            }

            var augmented = TryAugmentGuestJson(
                bytes,
                new ExecutionTargetInfo
                {
                    Kind = ExecutionTargetRef.WindowsSandboxDefault.Kind,
                    Id = ExecutionTargetRef.WindowsSandboxDefault.Id,
                    Architecture = target.Capabilities.Architecture,
                    Epoch = target.Epoch.Value,
                },
                processId);

            if (augmented is null)
            {
                WriteRawToConsole(Console.OpenStandardOutput(), bytes);
                return;
            }

            ansiConsole.Profile.Out.Writer.WriteLine(augmented);
        }

        /// <summary>
        /// Merges the additive execution-target members into a guest run result.
        /// </summary>
        /// <returns>
        /// The augmented document, or null when the payload must be relayed byte-for-byte instead.
        /// </returns>
        /// <remarks>
        /// A payload that does not parse — or that overran the capture bound — is relayed unchanged.
        /// Losing an additive field is recoverable; handing a caller a truncated or re-encoded
        /// document is not.
        /// </remarks>
        internal static string? TryAugmentGuestJson(
            byte[] payload,
            ExecutionTargetInfo executionTarget,
            int agentProcessId)
        {
            ArgumentNullException.ThrowIfNull(payload);

            if (payload.Length is 0 or >= MaxCapturedJsonBytes)
            {
                return null;
            }

            RunCommandResult? result;

            try
            {
                result = JsonSerializer.Deserialize(payload, RunCommandJsonContext.Default.RunCommandResult);
            }
            catch (JsonException)
            {
                return null;
            }

            if (result is null)
            {
                return null;
            }

            result.Sandbox = true;
            result.ProcessScope = "sandbox";
            result.ExecutionTarget = executionTarget;

            // The guest's own process ID when it reported one — that is the application. The agent's
            // child is only the winapp that launched it, and pointing a UI command at that would
            // target the wrong process.
            var appProcessId = result.ProcessId ?? (agentProcessId > 0 ? (uint)agentProcessId : null);

            if (appProcessId is { } pid)
            {
                result.AppTarget = $"sandbox:{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            return JsonSerializer.Serialize(result, RunCommandJsonContext.Default.RunCommandResult);
        }

        /// <summary>Relays a guest stream chunk verbatim.</summary>
        /// <remarks>
        /// Bytes rather than decoded text: guest output can be binary or split mid-character at a
        /// chunk boundary, and decoding per chunk would corrupt both.
        /// </remarks>
        private static void WriteRawToConsole(Stream stream, ReadOnlyMemory<byte> data)
        {
            stream.Write(data.Span);
            stream.Flush();
        }
    }
}

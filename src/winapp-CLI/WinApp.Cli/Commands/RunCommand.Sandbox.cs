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
                layout = outputAppXDirectory ?? new DirectoryInfo(
                    TargetPathSafety.CombineInsideRoot(inputFolder.FullName, "AppX"));

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
                        catch (Exception ex) when (ex is not OperationCanceledException)
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
                cancellationToken,
                // --with-alias is documented as running the app "in the current terminal with
                // inherited stdin/stdout/stderr". Output already came back; without this, stdin did
                // not, so a console app launched this way could never be driven.
                forwardStandardInput: withAlias);
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
                    Detach = detach,
                },
                cancellationToken,
                guestProducesRunResult: false);
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
        /// <param name="guestProducesRunResult">
        /// True when the request runs guest winapp, whose stdout is a <see cref="RunCommandResult"/>.
        /// False for a direct unpackaged executable, whose arbitrary stdout is suppressed under JSON
        /// and replaced with a host-built result envelope.
        /// </param>
        /// <param name="forwardStandardInput">
        /// Whether this process's standard input is streamed to the guest process. Set for
        /// <c>--with-alias</c>, which promises an inherited-stdio console run.
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
            bool guestProducesRunResult = true,
            bool forwardStandardInput = false)
        {
            try
            {
                await using var target = await executionTargetOrchestrator.PrepareAsync(
                    PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = requiresRealInput },
                    cancellationToken);

                WriteProgress(isJson, ExecutionTargetOrchestrator.DescribeProgress(target.Reused));

                var provisioning = await ProvisionRuntimesAsync(target, sourceRoot, cancellationToken);

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

                // A per-user .NET installation is discoverable to an apphost through DOTNET_ROOT and
                // nothing else without machine-wide registration, so the root provisioning created
                // has to reach the launched process itself. Merged rather than assigned, so the
                // owner context this launch also depends on is not lost — and only present at all
                // when the guest reported that the managed root is what satisfies a framework.
                foreach (var (name, value) in provisioning.LaunchEnvironment)
                {
                    ownerEnvironment[name] = value;
                }

                var startedProcessId = 0;

                // Under --json the guest's payload is captured rather than relayed, so the additive
                // execution-target members can be merged into the document the caller parses. It is
                // never reformatted blind: anything that does not parse is written through exactly
                // as the guest produced it, because corrupting a result is worse than omitting a
                // field.
                using var capturedOutput = isJson && guestProducesRunResult ? new MemoryStream() : null;
                var request = buildRequest(deployment, ownerEnvironment);

                var exitCode = await guestApplicationRunner.RunAsync(
                    target,
                    state,
                    request,
                    new GuestExecCallbacks(
                        OnOperationId: forwardStandardInput
                            ? GuestStandardInputPump.Attach(target.Channel, cancellationToken)
                            : null,
                        OnStarted: process =>
                        {
                            startedProcessId = process.ProcessId;
                        },
                        OnStandardOutput: data =>
                        {
                            if (isJson && !guestProducesRunResult)
                            {
                                return;
                            }

                            if (capturedOutput is not null)
                            {
                                CaptureBounded(capturedOutput, data);
                                return;
                            }

                            WriteRawToConsole(Console.OpenStandardOutput(), data);
                        },
                        OnStandardError: data =>
                        {
                            if (!isJson || guestProducesRunResult)
                            {
                                WriteRawToConsole(Console.OpenStandardError(), data);
                            }
                        }),
                    cancellationToken);

                if (isJson && guestProducesRunResult && capturedOutput is not null)
                {
                    PublishGuestJson(capturedOutput, target, startedProcessId);
                }
                else if (isJson)
                {
                    PublishDirectGuestJson(target);
                }

                return exitCode;
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

        /// <summary>
        /// Installs and verifies the shared runtimes the deployment needs, before it is deployed.
        /// </summary>
        /// <remarks>
        /// Ahead of deployment rather than after it, matching the spec's order: a guest missing the
        /// Windows App Runtime cannot register the package that is about to be copied there, and
        /// discovering that after transferring hundreds of megabytes helps nobody.
        /// <para>
        /// The structured failure is re-thrown rather than returned, because the status wrapper
        /// swallows exceptions from its task body and only the caller's envelope knows how to render
        /// an execution-target error under <c>--json</c>.
        /// </para>
        /// </remarks>
        private async Task<RuntimeProvisionResult> ProvisionRuntimesAsync(
            PreparedTarget target,
            DirectoryInfo sourceRoot,
            CancellationToken cancellationToken)
        {
            ExecutionTargetException? failure = null;
            RuntimeProvisionResult? provisioned = null;

            await statusService.ExecuteWithStatusAsync(
                "Preparing shared runtimes...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var result = await targetRuntimeService.EnsureAsync(
                            target,
                            ExecutionTargetRef.WindowsSandboxDefault,
                            sourceRoot,
                            new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()),
                            taskContext,
                            ct);

                        provisioned = result;

                        return (0, DescribeProvisioning(result));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Every failure is captured, not just the structured ones. An unexpected
                        // exception swallowed here would let the run continue to deploy and launch
                        // as though the runtime graph had been verified, which is the one outcome
                        // this step exists to make impossible.
                        failure = ex as ExecutionTargetException ?? ExecutionTargetException.Create(
                            ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                            $"The shared runtimes the app needs could not be provisioned in Windows Sandbox: {ex.Message}",
                            userAction: "Retry the command. If it keeps failing, close Windows Sandbox so a fresh guest is created.",
                            innerException: ex);

                        return (1, $"{UiSymbols.Error} {failure.Error.Message}");
                    }
                },
                cancellationToken);

            // Cancellation is swallowed by the status wrapper, so it is re-observed here rather than
            // left to look like a successful provisioning pass.
            cancellationToken.ThrowIfCancellationRequested();

            if (failure is not null)
            {
                throw failure;
            }

            // A null result with no captured failure would mean the status body neither completed
            // nor reported why. Treating that as "nothing to provision" would launch on an
            // unverified graph, so it fails instead.
            return provisioned ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                "winapp could not verify the shared runtimes the app needs inside Windows Sandbox.",
                userAction: "Retry the command. If it keeps failing, close Windows Sandbox so a fresh guest is created.");
        }

        /// <summary>Summarises one provisioning pass for the progress line.</summary>
        private static string DescribeProvisioning(RuntimeProvisionResult result)
        {
            if (result.Requirements.IsEmpty)
            {
                return "No shared runtimes required";
            }

            var installed = result.Report?.Items.Count(item => item.Installed) ?? 0;

            return installed == 0
                ? "Shared runtimes verified"
                : $"Installed {installed} shared runtime component(s)";
        }

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

        /// <summary>Publishes the additive result for a directly launched unpackaged guest app.</summary>
        internal void PublishDirectGuestJson(PreparedTarget target)
        {
            var result = CreateDirectGuestResult(
                target.Capabilities.Architecture,
                target.Epoch.Value);

            ansiConsole.Profile.Out.Writer.WriteLine(
                JsonSerializer.Serialize(result, RunCommandJsonContext.Default.RunCommandResult));
        }

        internal static RunCommandResult CreateDirectGuestResult(
            string architecture,
            string epoch)
        {
            // The agent starts a containment barrier first, so its ExecStarted PID is the barrier,
            // not the app. Omitting a target is safer than publishing a copyable PID for the wrong
            // process; discovery remains `ui list-windows --sandbox` or an explicit app name.
            return new RunCommandResult
            {
                Sandbox = true,
                ProcessScope = "sandbox",
                ExecutionTarget = new ExecutionTargetInfo
                {
                    Kind = ExecutionTargetRef.WindowsSandboxDefault.Kind,
                    Id = ExecutionTargetRef.WindowsSandboxDefault.Id,
                    Architecture = architecture,
                    Epoch = epoch,
                },
            };
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

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>A deployment that is in place in the guest and ready to be launched.</summary>
/// <param name="State">Committed, clean deployment state.</param>
/// <param name="PayloadPath">Absolute guest path holding the deployed application files.</param>
/// <param name="LayoutPath">Absolute guest path the guest registers the package from.</param>
internal sealed record GuestDeployment(DeploymentState State, string PayloadPath, string LayoutPath);

/// <summary>
/// Deploys an application into an execution target and runs it there through guest winapp
/// (spec §"Deployment model", §"Package ownership").
/// </summary>
/// <remarks>
/// Target-neutral: it uses only the command channel, the reported managed root, and deployment
/// state. Nothing here knows what a Sandbox is.
/// <para>
/// The guest is asked to perform the <em>ordinary</em> <c>winapp run</c>, so registration, runtime
/// provisioning, launch, debugging, and the whole option matrix are the same code a local run uses.
/// The host's job is only to get the right files into the guest and to relay the result.
/// </para>
/// </remarks>
internal sealed class GuestApplicationRunner(TargetDeploymentService deployments)
{
    /// <summary>
    /// Content that must never be deployed, however it came to be in the source folder.
    /// </summary>
    /// <remarks>
    /// An <c>.appxrecipe</c> lists build outputs by absolute <em>host</em> path. Guest winapp would
    /// find it, prefer it over the files actually present, resolve none of those paths, and register
    /// an empty layout — a silent wrong answer rather than a failure. Materialization does not
    /// normally leave one behind; this makes it impossible for one to matter.
    /// </remarks>
    internal static bool IsExcludedFromDeployment(string relativePath) =>
        relativePath.EndsWith(".appxrecipe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles <paramref name="sourceRoot"/> into the guest and returns where it landed.
    /// </summary>
    /// <param name="target">Prepared target, whose channel and epoch the deployment is fenced on.</param>
    /// <param name="deploymentId">Internal deployment identity.</param>
    /// <param name="sourceRoot">Host folder to deploy — a materialized layout or a build output.</param>
    /// <param name="clean">True to discard the guest copy and its registration layout first.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<GuestDeployment> DeployAsync(
        PreparedTarget target,
        string deploymentId,
        DirectoryInfo sourceRoot,
        bool clean,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceRoot);

        var payloadScope = GuestPaths.PayloadScope(deploymentId);
        var layoutScope = GuestPaths.LayoutScope(deploymentId);

        // Resolved before anything is transferred: a guest that cannot name its managed root cannot
        // be launched into, and discovering that after a multi-hundred-megabyte copy helps nobody.
        var payloadPath = GuestPaths.Resolve(target.Capabilities, payloadScope);
        var layoutPath = GuestPaths.Resolve(target.Capabilities, layoutScope);

        var snapshot = await DeploymentPlanner
            .CreateSnapshotAsync(sourceRoot, deploymentId, cancellationToken, IsExcludedFromDeployment)
            .ConfigureAwait(false);

        if (clean)
        {
            // The registration layout is guest-derived from the payload, so a clean redeploy that
            // left it in place would register the previous build's files.
            await target.Channel.DeleteScopeAsync(layoutScope, cancellationToken).ConfigureAwait(false);
        }

        var result = await deployments.ReconcileAsync(
            ExecutionTargetRef.WindowsSandboxDefault,
            target.Epoch,
            target.Channel,
            deploymentId,
            snapshot,
            sourceRoot,
            clean,
            cancellationToken).ConfigureAwait(false);

        TargetDeploymentService.EnsureLaunchable(result.State, target.Epoch);

        return new GuestDeployment(result.State, payloadPath, layoutPath);
    }

    /// <summary>
    /// Runs one guest winapp command for a deployment and relays its streams and exit code.
    /// </summary>
    /// <remarks>
    /// The started process is committed to deployment state before the command completes, so a host
    /// that dies mid-run still leaves a record of what it launched rather than a deployment that
    /// claims nothing is running.
    /// </remarks>
    public async Task<int> RunAsync(
        PreparedTarget target,
        DeploymentState state,
        GuestExecRequest request,
        GuestExecCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);

        var started = false;

        var result = await target.Channel.ExecuteAsync(
            request,
            callbacks with
            {
                OnStarted = process =>
                {
                    if (started)
                    {
                        return;
                    }

                    started = true;
                    TryCommitProcess(target, state, process);
                    callbacks.OnStarted?.Invoke(process);
                },
            },
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode;
    }

    /// <summary>Records which guest package this deployment owns.</summary>
    public DeploymentState CommitPackage(DeploymentState state, PackageOwnership package) =>
        deployments.CommitPackage(ExecutionTargetRef.WindowsSandboxDefault, state, package);

    /// <summary>
    /// Finds the deployment in this generation that owns <paramref name="packageName"/>.
    /// </summary>
    /// <remarks>
    /// Matching is on what a deployment actually registered, not on a name a caller supplied, and is
    /// restricted to the current generation because a record from a previous one describes a guest
    /// that no longer exists. An identity owned by more than one live deployment is reported rather
    /// than resolved by picking one — the alternative is unregistering an application the user did
    /// not name.
    /// </remarks>
    public DeploymentState? FindOwningDeployment(
        ExecutionTargetEpoch epoch,
        string packageName,
        string publisher)
    {
        var owning = deployments
            .List(ExecutionTargetRef.WindowsSandboxDefault)
            .Where(state => state.IsForEpoch(epoch)
                && state.Package is { } package
                && string.Equals(package.PackageName, packageName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(package.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (owning.Count <= 1)
        {
            return owning.SingleOrDefault();
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetAmbiguous,
            $"More than one application deployed in Windows Sandbox is registered as '{packageName}'.",
            userAction: "Unregister them from the directories they were run from, one at a time.",
            context: new Dictionary<string, string>
            {
                ["deployments"] = string.Join(", ", owning.Select(state => state.DeploymentId)),
            });
    }

    /// <summary>Forgets the package a deployment owned, after it has been unregistered.</summary>
    public DeploymentState ClearPackage(DeploymentState state) =>
        deployments.CommitPackage(ExecutionTargetRef.WindowsSandboxDefault, state, package: null);

    /// <summary>
    /// Commits the launched process without letting a state race fail a running application.
    /// </summary>
    /// <remarks>
    /// The process is already running by this point. A concurrent commit from another host process
    /// means the record is stale, which is a diagnostics loss, not a reason to report a successful
    /// launch as a failure.
    /// </remarks>
    private void TryCommitProcess(PreparedTarget target, DeploymentState state, GuestProcessStart process)
    {
        try
        {
            deployments.CommitProcess(
                ExecutionTargetRef.WindowsSandboxDefault,
                state,
                process.ProcessId,
                process.StartTicksUtc);
        }
        catch (ExecutionTargetException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not record the launched guest process for deployment {0}: {1}",
                state.DeploymentId,
                ex.Message);
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Brings a file a guest command produced back to the host path the caller asked for
/// (spec §"Artifact handling").
/// </summary>
/// <remarks>
/// The requested destination is written only after the whole file has arrived and its size and hash
/// match what the guest reported. An interrupted transfer therefore never appears as a shorter but
/// plausible result — the caller sees a failure naming the artifact, its expected size, and how much
/// arrived, and whatever was already at the destination is untouched.
/// <para>
/// v1 restarts a transfer from the beginning rather than resuming. Resumption would need the guest
/// to keep an artifact alive across connections, which is a larger promise than a screenshot or a
/// recording needs.
/// </para>
/// </remarks>
internal sealed class TargetArtifactService
{
    /// <summary>The guest staging folder for one operation's outputs.</summary>
    public static GuestPathScope ScopeFor(Guid operationId) =>
        new(GuestRootNames.Artifacts, operationId.ToString("n", CultureInfo.InvariantCulture));

    /// <summary>
    /// Fetches <paramref name="artifact"/> from guest staging and publishes it atomically.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// The guest produced nothing, produced something that failed verification, or the transfer was
    /// interrupted. Nothing is published in any of those cases.
    /// </exception>
    public static async Task PublishAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        RoutedArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(artifact);

        var staged = await channel.ListFilesAsync(scope, cancellationToken).ConfigureAwait(false);

        var declared = staged.FirstOrDefault(file =>
            string.Equals(file.RelativePath, artifact.GuestRelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed,
                $"The command reported success but produced no '{artifact.GuestRelativePath}' in Windows Sandbox.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string> { ["artifact"] = artifact.GuestRelativePath });

        var directory = Path.GetDirectoryName(artifact.HostDestination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A command's output is timestamped when it was produced here, not in the guest, so the
        // guest's last-write time is deliberately not carried over.
        await GuestFilePull.ReceiveAsync(
            channel, scope, declared, artifact.HostDestination, applyGuestTimestamp: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Discards an operation's guest staging, best effort.</summary>
    /// <remarks>
    /// A staging folder left behind wastes guest disk until the Sandbox is stopped; failing a
    /// command that already produced its result over that would be a worse trade.
    /// </remarks>
    public static async Task TryRemoveAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        try
        {
            await channel.DeleteScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExecutionTargetException or OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not remove guest artifact staging '{0}': {1}", scope.Scope, ex.Message);
        }
    }
}

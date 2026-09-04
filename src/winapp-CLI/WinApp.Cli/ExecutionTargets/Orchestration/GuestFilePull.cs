// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Brings one file out of a guest and onto a host path, publishing it only once it is proven to be
/// exactly what the guest declared (spec §"Artifact handling").
/// </summary>
/// <remarks>
/// Both ways of pulling a file out of a target — a command's routed artifact and an explicit
/// <c>winapp target pull</c> — need the same guarantee, so they share this one implementation. The
/// destination is written by a single rename after the whole file has arrived and its size and hash
/// match, so an interrupted transfer never appears as a shorter but plausible result and whatever
/// was already at the destination is left untouched.
/// </remarks>
internal static class GuestFilePull
{
    /// <summary>How much is read at a time, and the size of the temporary's write buffer.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Receives <paramref name="declared"/> from guest staging and publishes it to
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="applyGuestTimestamp">
    /// Whether the published file keeps the guest's last-write time, so a user can see at a glance
    /// what a pull changed. A command's output does not, because its timestamp is when it was
    /// produced here. Stamping happens after the file is published and is best effort: the content
    /// is already proven, so a failed stamp must not be reported as a failed transfer.
    /// </param>
    /// <exception cref="ExecutionTargetException">
    /// The transfer was interrupted or what arrived did not match. Nothing is published in either
    /// case, and the temporary is removed.
    /// </exception>
    public static async Task ReceiveAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        GuestFileInfo declared,
        string destination,
        bool applyGuestTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        // A sibling of the destination, so publishing is a rename within one volume and cannot
        // leave a half-written file where a complete one is expected.
        var temporary = $"{destination}.{Guid.NewGuid():n}.part";
        var received = 0L;

        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await channel.GetFileAsync(scope, declared.RelativePath, stream, cancellationToken)
                    .ConfigureAwait(false);

                received = stream.Length;
            }

            await VerifyAsync(temporary, declared, cancellationToken).ConfigureAwait(false);

            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temporary);

            if (ex is ExecutionTargetException or OperationCanceledException)
            {
                throw;
            }

            // A raw IOException here reads as a winapp defect rather than an interrupted copy, so
            // every non-target failure is reported as the transfer failure it is.
            throw Interrupted(
                $"'{declared.RelativePath}' could not be copied out of Windows Sandbox.",
                declared,
                received,
                phase: "transfer",
                ex);
        }

        if (applyGuestTimestamp)
        {
            TryApplyTimestamp(destination, declared.LastWriteUtcTicks);
        }
    }

    /// <summary>
    /// Stamps the guest's last-write time onto a file that is already published.
    /// </summary>
    /// <remarks>
    /// Deliberately outside the publish, and deliberately best effort. By this point the content has
    /// been verified and renamed into place, so reporting an interrupted transfer would be a lie —
    /// the caller would believe its previous file survived when it has already been replaced.
    /// <para>
    /// No winapp decision reads this timestamp: a pull always re-copies every matched file, and a
    /// push compares content hashes rather than times. Losing it costs the user only the ability to
    /// see at a glance what changed, which is not worth failing a completed transfer over.
    /// </para>
    /// <para>
    /// The tick count comes off the wire, so a value outside <see cref="DateTime"/>'s range is
    /// guest-supplied input rather than a defect and must not fault a completed transfer.
    /// </para>
    /// </remarks>
    private static void TryApplyTimestamp(string destination, long lastWriteUtcTicks)
    {
        try
        {
            File.SetLastWriteTimeUtc(destination, new DateTime(lastWriteUtcTicks, DateTimeKind.Utc));
        }
        catch (Exception ex) when (
            ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not stamp the guest's last-write time onto '{0}': {1}", destination, ex.Message);
        }
    }

    /// <summary>
    /// Proves a received file is exactly what the guest declared, before anything is published.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the guarantee can be verified directly: content changing
    /// between the guest's hash and the host's read is not something a test can schedule from the
    /// outside, and a guarantee that is only asserted through a happy path is not asserted at all.
    /// </remarks>
    internal static async Task VerifyAsync(
        string path,
        GuestFileInfo declared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var length = new FileInfo(path).Length;

        if (length != declared.Size)
        {
            throw Interrupted(NotIntact(declared), declared, length, phase: "size");
        }

        var hash = await GuestFileService.ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(hash, declared.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Interrupted(NotIntact(declared), declared, length, phase: "hash");
        }
    }

    private static string NotIntact(GuestFileInfo declared) =>
        $"'{declared.RelativePath}' did not arrive intact from Windows Sandbox.";

    private static ExecutionTargetException Interrupted(
        string message,
        GuestFileInfo declared,
        long received,
        string phase,
        Exception? innerException = null) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransferInterrupted,
            message,
            userAction: "Retry the command.",
            context: new Dictionary<string, string>
            {
                ["artifact"] = declared.RelativePath,
                ["expectedBytes"] = declared.Size.ToString(CultureInfo.InvariantCulture),
                ["receivedBytes"] = received.ToString(CultureInfo.InvariantCulture),
                ["phase"] = phase,
            },
            innerException: innerException);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup failure must never turn an interrupted transfer into a success, and the
            // caller is already failing.
            System.Diagnostics.Trace.TraceWarning("Could not remove '{0}': {1}", path, ex.Message);
        }
    }
}

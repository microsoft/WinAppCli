// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>What a copy actually did.</summary>
/// <param name="Transferred">Files whose content moved.</param>
/// <param name="Skipped">Files already identical at the destination.</param>
/// <param name="Bytes">Total bytes transferred.</param>
internal sealed record SandboxCopyResult(int Transferred, int Skipped, long Bytes);

/// <summary>
/// Copies files and directories between the host and managed guest storage
/// (spec §"File copy").
/// </summary>
/// <remarks>
/// Built on the same channel primitives deployment uses, so hashing, verification, atomic
/// replacement, and containment behave identically whether a file arrived through
/// <c>winapp run --sandbox</c> or <c>winapp sandbox cp</c>. A second transfer path would be a second
/// set of bugs.
/// <para>
/// Unchanged files are skipped by content hash rather than timestamp, because the point of skipping
/// is to make a repeated copy cheap without ever leaving stale content behind.
/// </para>
/// </remarks>
internal static class SandboxCopyService
{
    /// <summary>Guest scope arbitrary copies land in.</summary>
    /// <remarks>
    /// Copies address the general-purpose work area rather than a deployment, so a `cp` can never
    /// disturb a deployment's exact desired state and cause its next reconciliation to fight it.
    /// </remarks>
    internal static GuestPathScope WorkScope { get; } = new(GuestRootNames.Work, Scope: null);

    /// <summary>Performs a parsed copy.</summary>
    public static Task<SandboxCopyResult> CopyAsync(
        GuestCommandChannel channel,
        SandboxCopyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);

        return request.Direction == SandboxCopyDirection.ToGuest
            ? CopyToGuestAsync(channel, request, cancellationToken)
            : CopyFromGuestAsync(channel, request, cancellationToken);
    }

    private static async Task<SandboxCopyResult> CopyToGuestAsync(
        GuestCommandChannel channel,
        SandboxCopyRequest request,
        CancellationToken cancellationToken)
    {
        var sources = EnumerateHostSources(request.HostPath, cancellationToken, out var sourceRoot);
        var existing = await channel.ListFilesAsync(WorkScope, cancellationToken).ConfigureAwait(false);

        var existingByPath = existing.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        var transferred = 0;
        var skipped = 0;
        var bytes = 0L;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = CombineGuestRelativePath(request.GuestPath, sourceRoot, source.FullName);

            // Re-proven immediately before the file is read, so a junction planted between the walk
            // and the copy is refused rather than quietly sending content from outside the source.
            if (sourceRoot.Length > 0 && Directory.Exists(sourceRoot))
            {
                HostSourceWalker.EnsureNoReparseAncestor(sourceRoot, source.FullName);
            }

            var hash = await GuestFileService.ComputeHashAsync(source.FullName, cancellationToken)
                .ConfigureAwait(false);

            if (existingByPath.TryGetValue(relativePath, out var already) &&
                string.Equals(already.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            await using var content = new FileStream(
                source.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);

            await channel.PutFileAsync(
                WorkScope,
                new GuestFileInfo(relativePath, source.Length, source.LastWriteTimeUtc.Ticks, hash),
                content,
                cancellationToken).ConfigureAwait(false);

            transferred++;
            bytes += source.Length;
        }

        return new SandboxCopyResult(transferred, skipped, bytes);
    }

    private static async Task<SandboxCopyResult> CopyFromGuestAsync(
        GuestCommandChannel channel,
        SandboxCopyRequest request,
        CancellationToken cancellationToken)
    {
        var guestFiles = await channel.ListFilesAsync(WorkScope, cancellationToken).ConfigureAwait(false);
        var prefix = NormalizeGuestRelative(request.GuestPath);

        var matches = guestFiles
            .Where(f => IsUnderPrefix(f.RelativePath, prefix))
            .ToList();

        if (matches.Count == 0)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed,
                $"Nothing at '{request.GuestPath}' in the Sandbox to copy.",
                userAction: "Check the path, then retry.",
                context: new Dictionary<string, string> { ["guestPath"] = request.GuestPath });
        }

        var transferred = 0;
        var bytes = 0L;

        foreach (var file in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = ResolveHostDestination(request.HostPath, prefix, file.RelativePath, matches.Count);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Received into a temporary and verified before the destination is touched, so an
            // interrupted copy never publishes a partial file over something that was correct.
            var temporary = $"{destination}.{Guid.NewGuid():n}.part";

            try
            {
                await using (var stream = new FileStream(
                    temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    await channel.GetFileAsync(WorkScope, file.RelativePath, stream, cancellationToken)
                        .ConfigureAwait(false);
                }

                await VerifyAsync(temporary, file, cancellationToken).ConfigureAwait(false);

                File.Move(temporary, destination, overwrite: true);
                File.SetLastWriteTimeUtc(destination, new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc));
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            transferred++;
            bytes += file.Size;
        }

        return new SandboxCopyResult(transferred, Skipped: 0, bytes);
    }

    /// <summary>Proves what arrived is what the guest said it was sending.</summary>
    private static async Task VerifyAsync(
        string path,
        GuestFileInfo expected,
        CancellationToken cancellationToken)
    {
        var actualSize = new FileInfo(path).Length;
        var actualHash = await GuestFileService.ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);

        if (actualSize == expected.Size &&
            string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransferInterrupted,
            $"Copying '{expected.RelativePath}' out of the Sandbox did not complete.",
            userAction: "Retry the command.",
            context: new Dictionary<string, string>
            {
                ["relativePath"] = expected.RelativePath,
                ["expectedSize"] = expected.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["receivedBytes"] = actualSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["phase"] = "verify",
            });
    }

    /// <summary>Expands a host file or directory into the files to send.</summary>
    /// <remarks>
    /// The expansion is a manual no-follow walk. Following a directory junction here would copy
    /// files the caller never named into the guest while every one of them passed a file-level
    /// reparse check, because a file reached through a junction is an ordinary file.
    /// <para>
    /// Internal rather than private so the containment property can be asserted directly: what
    /// matters is that a file behind a link is never enumerated, and a test that had to stand up a
    /// guest transport to observe that would be testing the transport instead.
    /// </para>
    /// </remarks>
    internal static List<FileInfo> EnumerateHostSources(
        string hostPath,
        CancellationToken cancellationToken,
        out string sourceRoot)
    {
        if (File.Exists(hostPath))
        {
            var file = new FileInfo(hostPath);

            // A named file gets the same rule the walk applies to everything else: copying through a
            // link would put content the caller did not name into the guest.
            if (HostSourceWalker.IsLink(file))
            {
                throw LinkRefused(hostPath);
            }

            sourceRoot = Path.GetDirectoryName(hostPath) ?? string.Empty;
            return [file];
        }

        if (Directory.Exists(hostPath))
        {
            var directory = new DirectoryInfo(hostPath);

            if (HostSourceWalker.IsLink(directory))
            {
                throw LinkRefused(hostPath);
            }

            sourceRoot = Path.TrimEndingDirectorySeparator(hostPath);

            // Manual no-follow walk: SearchOption.AllDirectories descends through junctions, and the
            // ordinary files behind one carry no reparse attribute, so they would be copied into the
            // guest as if they had been inside the folder the caller asked to copy. Links are treated
            // as absent here, matching the guest-side rule that nothing inside a managed root may
            // redirect elsewhere.
            return HostSourceWalker.EnumerateFiles(sourceRoot, HostReparsePolicy.Skip, cancellationToken);
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.ArtifactFailed,
            $"'{hostPath}' does not exist.",
            userAction: "Check the path, then retry.",
            context: new Dictionary<string, string> { ["hostPath"] = hostPath });
    }

    /// <summary>Refuses a source that is itself a link.</summary>
    private static ExecutionTargetException LinkRefused(string hostPath) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.ArtifactFailed,
            $"'{hostPath}' is a symbolic link or junction, so copying it into the Sandbox was refused.",
            userAction: "Copy the real file or folder instead.",
            context: new Dictionary<string, string> { ["hostPath"] = hostPath });

    /// <summary>Builds the guest-relative path one source file should land at.</summary>
    private static string CombineGuestRelativePath(string guestPath, string sourceRoot, string sourceFile)
    {
        var target = NormalizeGuestRelative(guestPath);

        if (sourceRoot.Length == 0 || !sourceFile.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var relative = sourceFile[sourceRoot.Length..].TrimStart(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // A single file keeps the destination name it was given; a directory preserves its own
        // structure beneath it.
        return File.Exists(sourceRoot) || relative.Length == 0
            ? target
            : Path.Join(target, relative);
    }

    /// <summary>Where one guest file lands on the host.</summary>
    internal static string ResolveHostDestination(
        string hostPath,
        string prefix,
        string guestRelativePath,
        int matchCount)
    {
        // A single file copied to a path that is not an existing directory keeps that exact name;
        // anything else preserves structure beneath the destination directory.
        if (matchCount == 1 && !Directory.Exists(hostPath))
        {
            return hostPath;
        }

        var relative = guestRelativePath.Length > prefix.Length
            ? guestRelativePath[prefix.Length..].TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(guestRelativePath);

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return TargetPathSafety.CombineInsideRoot(hostPath, segments);
    }

    /// <summary>Reduces a guest path to a relative form inside managed storage.</summary>
    /// <remarks>
    /// Guest paths are written the way a user thinks of them, including drive-qualified forms. They
    /// are reduced to a relative path here so the guest resolves them against a managed root and can
    /// prove containment — a guest-provided path never selects an arbitrary location.
    /// </remarks>
    internal static string NormalizeGuestRelative(string guestPath)
    {
        var path = guestPath.Replace('/', '\\').Trim();

        if (path.Length >= 2 && path[1] == ':')
        {
            path = path[2..];
        }

        return path.TrimStart('\\');
    }

    private static bool IsUnderPrefix(string relativePath, string prefix) =>
        prefix.Length == 0 ||
        string.Equals(relativePath, prefix, StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup failure must never turn an interrupted copy into a success, so it stays
            // best-effort and is only traced.
            System.Diagnostics.Trace.TraceWarning(
                "Could not remove the incomplete copy '{0}': {1}", path, ex.Message);
        }
    }
}

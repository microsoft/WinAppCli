// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>What a host walk does when it meets a reparse point.</summary>
internal enum HostReparsePolicy
{
    /// <summary>
    /// Refuse the whole operation. Used by deployment, where a link in the source means the guest
    /// would not be a copy of the folder the developer named, and silently deploying something else
    /// is worse than saying so.
    /// </summary>
    Reject,

    /// <summary>
    /// Treat the entry as absent. Used by <c>sandbox cp</c>, matching the guest-side rule that
    /// nothing inside a managed root may redirect elsewhere.
    /// </summary>
    Skip,
}

/// <summary>
/// The single no-follow walk over a host folder that is about to be hashed, deployed, or copied
/// into a guest.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SearchOption.AllDirectories"/> follows directory junctions and symbolic links. That
/// makes a per-file reparse check useless as a containment defence: the junction is the directory,
/// and the ordinary files reached through it carry no reparse attribute at all, so every one of them
/// passes a file-only check while actually living outside the root. A junction named
/// <c>build\logs</c> pointing at <c>C:\Users\me\.ssh</c> would be enumerated, hashed, and copied
/// into the guest as <c>build\logs\id_rsa</c>.
/// </para>
/// <para>
/// So the walk is manual and one level at a time, and every directory is tested <em>before</em> it
/// is descended into. That also makes a self-referencing junction terminate immediately rather than
/// recursing until the path length or the stack gives out: the loop edge is itself a reparse point,
/// so it is refused at the point it would have been followed.
/// </para>
/// <para>
/// <b>Threat model.</b> This is containment against links that exist, and against a link planted
/// between the walk and the read — <see cref="EnsureNoReparseAncestor"/> re-checks the chain
/// immediately before each file is opened. It is not a handle-relative TOCTOU proof: a mutually
/// trusted same-user process that wins a race in the window between that re-check and the open can
/// still swap a directory. Closing that completely needs handle-relative opens
/// (<c>FILE_FLAG_OPEN_REPARSE_POINT</c> on every component), which is out of scope here and
/// documented as a known limit in <c>docs/sandbox-execution.md</c>.
/// </para>
/// </remarks>
internal static class HostSourceWalker
{
    /// <summary>
    /// Enumerates every ordinary file beneath <paramref name="rootPath"/> without following links.
    /// </summary>
    /// <param name="rootPath">Canonical, separator-trimmed root. Never descended out of.</param>
    /// <param name="policy">What to do with a reparse point.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Files in a stable, case-insensitive full-path order.</returns>
    /// <exception cref="ExecutionTargetException">
    /// <paramref name="policy"/> is <see cref="HostReparsePolicy.Reject"/> and a link was found, or
    /// an entry resolved outside the root.
    /// </exception>
    public static List<FileInfo> EnumerateFiles(
        string rootPath,
        HostReparsePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var files = new List<FileInfo>();

        Collect(root, root, policy, files, cancellationToken);

        files.Sort((left, right) =>
            string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase));

        return files;
    }

    /// <summary>Walks exactly one directory level, refusing to descend through a reparse point.</summary>
    private static void Collect(
        string root,
        string directory,
        HostReparsePolicy policy,
        List<FileInfo> files,
        CancellationToken cancellationToken)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked for every entry, directory and file alike, and before any decision to descend.
            // This single test is what the whole class exists for.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (policy is HostReparsePolicy.Reject)
                {
                    throw LinkRejected(entry);
                }

                continue;
            }

            if (entry is DirectoryInfo child)
            {
                Collect(root, child.FullName, policy, files, cancellationToken);
                continue;
            }

            // Structurally guaranteed by descending only into real directories from the root, and
            // asserted anyway: containment is the property this walk is claimed to have, so it is
            // proven rather than assumed.
            if (!TargetPathSafety.IsInsideRoot(root, entry.FullName))
            {
                throw Escaped(entry.FullName);
            }

            files.Add((FileInfo)entry);
        }
    }

    /// <summary>
    /// Re-proves that no directory between <paramref name="rootPath"/> and
    /// <paramref name="fullPath"/> became a link since the walk.
    /// </summary>
    /// <remarks>
    /// Called immediately before the file is opened for hashing or copying, so the window in which a
    /// planted junction goes unnoticed is the open itself rather than the whole enumerate-then-read
    /// pass. Deliberately re-stats instead of reusing enumeration attributes, which would defeat the
    /// point.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// An ancestor is a link, or the path is not inside the root.
    /// </exception>
    public static void EnsureNoReparseAncestor(string rootPath, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        if (!TargetPathSafety.IsInsideRoot(root, fullPath))
        {
            throw Escaped(fullPath);
        }

        var current = root;

        // The final segment is the file itself; its own reparse state is checked by the caller's
        // enumeration and again by the read, so only the directories leading to it are walked here.
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).SkipLast(1))
        {
            current = Path.Join(current, segment);

            var info = new DirectoryInfo(current);

            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw LinkRejected(info);
            }
        }
    }

    /// <summary>Whether a path is a link, for callers that hold a single named source.</summary>
    /// <remarks>
    /// <c>sandbox cp</c> can be pointed straight at one file. That file never goes through
    /// <see cref="EnumerateFiles"/>, so the same rule is applied to it here rather than left to the
    /// directory case alone.
    /// </remarks>
    public static bool IsLink(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static ExecutionTargetException LinkRejected(FileSystemInfo entry) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            $"'{entry.Name}' is a symbolic link or junction, which cannot be deployed into the guest.",
            userAction: "Replace the link with the real file or folder, then rebuild.",
            context: new Dictionary<string, string> { ["fileName"] = entry.Name });

    private static ExecutionTargetException Escaped(string fullPath) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            "A file resolved outside the folder being deployed and was refused.",
            userAction: "Remove links from the folder, then retry.",
            context: new Dictionary<string, string> { ["fileName"] = Path.GetFileName(fullPath) });
}

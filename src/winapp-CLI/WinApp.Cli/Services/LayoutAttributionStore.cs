// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.Services;

/// <summary>
/// Records which files inside a loose-layout directory winapp itself staged, so a later run can
/// remove the ones the app no longer contains without touching anything else in that directory.
/// </summary>
/// <remarks>
/// A loose layout can be a directory the user named with <c>--output-appx-directory</c>, and that
/// directory may already hold files winapp knows nothing about. Reconciling it against the build
/// alone cannot tell "left over from the previous build" from "the user put it there", so pruning
/// on that basis is data loss. This store supplies the missing half: only a path winapp recorded as
/// staged is ever a deletion candidate.
/// <para>
/// The record lives in winapp's own state directory rather than inside the layout. A file in the
/// layout would have to be suppressed from both MSIX packaging and loose-layout registration, and
/// getting either wrong ships winapp's bookkeeping to users as app payload. Keeping it outside makes
/// that structurally impossible, at the cost of a stale record when a layout is deleted by hand —
/// which is harmless, because a recorded path that no longer exists is simply nothing to delete.
/// </para>
/// </remarks>
internal sealed class LayoutAttributionStore
{
    /// <summary>Identifies the file format, so an unrecognized or future record is ignored rather than misread.</summary>
    private const string HeaderToken = "winapp-layout-attribution/1";

    private const string StateDirectoryName = "layout-attribution";

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    private readonly DirectoryInfo _stateDirectory;

    internal LayoutAttributionStore(DirectoryInfo winappStateRoot)
    {
        _stateDirectory = new DirectoryInfo(Path.Combine(winappStateRoot.FullName, StateDirectoryName));
    }

    /// <summary>
    /// The identity a layout is keyed by: its full path with any trailing separator removed.
    /// </summary>
    internal static string CanonicalizeLayoutPath(string layoutPath)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(layoutPath));

    /// <summary>
    /// Hashes the canonical layout path into a fixed-length file name. Hashing (rather than encoding
    /// the path) keeps the state file name short enough to never hit MAX_PATH for a deep layout, and
    /// avoids having to escape characters that are legal in a path but not in a file name.
    /// </summary>
    private static string KeyFor(string canonicalLayoutPath)
    {
        // Windows paths compare case-insensitively, so the key must too, or the same layout reached
        // through differently-cased spellings would get two independent records.
        var bytes = Encoding.UTF8.GetBytes(canonicalLayoutPath.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private string PathFor(string canonicalLayoutPath, string extension)
        => Path.Combine(_stateDirectory.FullName, KeyFor(canonicalLayoutPath) + extension);

    internal FileInfo StateFileFor(string layoutPath)
        => new(PathFor(CanonicalizeLayoutPath(layoutPath), ".paths"));

    /// <summary>
    /// Takes a cross-process exclusive lock covering one layout, held for the whole
    /// read-copy-prune-write cycle so two concurrent winapp runs cannot interleave and leave the
    /// record describing neither run's result.
    /// </summary>
    /// <exception cref="TimeoutException">The lock was held by another process for too long.</exception>
    internal IDisposable AcquireLock(string layoutPath, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var canonical = CanonicalizeLayoutPath(layoutPath);
        Directory.CreateDirectory(_stateDirectory.FullName);

        var lockPath = PathFor(canonical, ".lock");
        var deadline = DateTime.UtcNow + (timeout ?? DefaultLockTimeout);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // DeleteOnClose keeps the state directory from accumulating a lock file per layout
                // ever built on this machine.
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                // Briefly observable while another process's DeleteOnClose handle is being torn down.
                Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TimeoutException(
                    $"Timed out waiting for another winapp process to finish with the layout at '{canonical}'. " +
                    "If no other winapp run is in progress, delete the stale lock file at " +
                    $"'{lockPath}' and try again.");
            }
        }
    }

    /// <summary>
    /// Returns the paths, relative to the layout root, that winapp staged there on its last
    /// successful run. An absent, unreadable, or unrecognized record yields an empty set, which
    /// makes this fail safe: with nothing attributed, nothing is a deletion candidate.
    /// </summary>
    internal HashSet<string> Read(string layoutPath)
    {
        var canonical = CanonicalizeLayoutPath(layoutPath);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var file = new FileInfo(PathFor(canonical, ".paths"));

        if (!file.Exists)
        {
            return result;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file.FullName, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        // Header line, then the layout it describes, then one relative path per line.
        if (lines.Length < 2 || !string.Equals(lines[0], HeaderToken, StringComparison.Ordinal))
        {
            return result;
        }

        // A hash collision, or a state directory copied between machines, would otherwise let one
        // layout's record drive deletions in a different layout.
        if (!string.Equals(lines[1], canonical, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        for (var i = 2; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                result.Add(lines[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces the record for a layout atomically, so an interrupted write leaves the previous
    /// record intact rather than a truncated one that would under-attribute (leaking stale files) or
    /// mis-attribute.
    /// </summary>
    internal void Write(string layoutPath, IEnumerable<string> relativePaths)
    {
        var canonical = CanonicalizeLayoutPath(layoutPath);
        Directory.CreateDirectory(_stateDirectory.FullName);

        var destination = PathFor(canonical, ".paths");

        var builder = new StringBuilder();
        builder.Append(HeaderToken).Append('\n');
        builder.Append(canonical).Append('\n');
        foreach (var path in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            // A path containing a newline would corrupt the line-per-path format on read. Windows
            // paths cannot contain one, so this only guards against a caller passing something else.
            if (path.Contains('\n') || path.Contains('\r'))
            {
                continue;
            }

            builder.Append(path).Append('\n');
        }

        // Same directory as the destination, so the replace is a rename within one volume.
        var temporary = destination + "." + Environment.ProcessId.ToString() + ".tmp";

        try
        {
            File.WriteAllText(temporary, builder.ToString(), Encoding.UTF8);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the record is not worth failing a materialization that succeeded: the next run
            // simply finds nothing attributed and prunes nothing, which is the safe direction.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Nothing further to do; the temp file is inert.
            }
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Validates execution alias names from AppX manifests and resolves them to the
/// canonical Windows App Execution Alias location under
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>.
/// </summary>
/// <remarks>
/// Manifest <c>&lt;uap5:ExecutionAlias Alias="..."/&gt;</c> values are
/// attacker-controlled when the user runs <c>winapp run --with-alias</c> in an
/// untrusted repository. Passing a bare filename like <c>"a.exe"</c> directly to
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// with <c>UseShellExecute = false</c> dispatches to <c>CreateProcessW</c>,
/// which resolves bare filenames against the current working directory first.
/// An attacker who ships a hostile <c>a.exe</c> next to a malicious manifest
/// would have it executed in place of the real Windows App Execution Alias
/// proxy. This resolver:
/// <list type="bullet">
///   <item>Rejects any alias that is not a bare filename (path separators,
///         <c>..</c>, drive letters, UNC paths, control chars).</item>
///   <item>Returns the absolute path under the WindowsApps folder so callers
///         can pass it to <c>ProcessStartInfo.FileName</c> directly. Absolute
///         paths bypass <c>CreateProcess</c>'s CWD/PATH search.</item>
/// </list>
/// </remarks>
internal static class ExecutionAliasResolver
{
    /// <summary>
    /// Returns true when <paramref name="alias"/> is a safe bare filename
    /// suitable for resolving under the WindowsApps alias directory.
    /// </summary>
    /// <remarks>
    /// Rejects null/empty/whitespace, any path separator
    /// (<see cref="Path.DirectorySeparatorChar"/> or
    /// <see cref="Path.AltDirectorySeparatorChar"/>), rooted paths (drive
    /// letters, UNC, leading separators), <c>..</c> path components, the bare
    /// dot/double-dot names, any character in
    /// <see cref="Path.GetInvalidFileNameChars"/> (which includes NUL and
    /// other control chars on Windows), and names longer than 255 characters.
    /// </remarks>
    public static bool IsSafeAliasName(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        if (alias.Length > 255)
        {
            return false;
        }

        if (alias is "." or "..")
        {
            return false;
        }

        if (alias.Contains(Path.DirectorySeparatorChar)
            || alias.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        if (Path.IsPathRooted(alias))
        {
            return false;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            if (alias.Contains(invalid))
            {
                return false;
            }
        }

        // Defence in depth: GetFileName strips any path components. If the
        // result differs from the input, the input contained path-like content
        // that the checks above missed (e.g. future platform differences).
        if (!string.Equals(Path.GetFileName(alias), alias, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the default Windows App Execution Alias base directory:
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>.
    /// </summary>
    public static string GetDefaultWindowsAppsDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");

    /// <summary>
    /// Resolves <paramref name="alias"/> to an absolute <see cref="FileInfo"/>
    /// under the supplied <paramref name="baseDirectory"/> (or the default
    /// WindowsApps location when <paramref name="baseDirectory"/> is null).
    /// Returns null when the alias is not a safe bare filename.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="FileInfo"/>'s <c>Exists</c> property indicates
    /// whether Windows has actually registered an alias proxy at that path —
    /// callers should check it before launching.
    /// </remarks>
    public static FileInfo? ResolveAliasPath(string? alias, string? baseDirectory = null)
    {
        if (!IsSafeAliasName(alias))
        {
            return null;
        }

        var dir = baseDirectory ?? GetDefaultWindowsAppsDirectory();
        return new FileInfo(Path.Combine(dir, alias!));
    }
}

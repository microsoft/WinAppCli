// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Resolves the coordination file set for the current user and Windows session (spec §7):
/// <code>
/// %LOCALAPPDATA%\Microsoft\WinAppCli\locks\
///   interactive-desktop-{session}.state.lock
///   interactive-desktop-{session}.state.json
///   interactive-desktop-{session}.active.lock
///   participants\interactive-desktop-{session}-{pid}-{startTicks}.lease
/// </code>
/// </summary>
/// <remarks>
/// The session scope matters because Windows gives every signed-in session its own foreground window,
/// focus and input stream. Two sessions on one machine (fast user switching, several RDP sessions) do
/// not interfere, so they must not queue behind each other.
/// </remarks>
internal interface IInteractiveDesktopPaths
{
    /// <summary>Directory holding the lock, state and participants for this user + session.</summary>
    string LockDirectory { get; }

    /// <summary>Directory holding one lease file per queued or active participant.</summary>
    string ParticipantsDirectory { get; }

    /// <summary>Short lock taken around every state read or update.</summary>
    string StateLockPath { get; }

    /// <summary>The coordination state document.</summary>
    string StatePath { get; }

    /// <summary>Long lock held only across a desktop-sensitive section.</summary>
    string ActiveLockPath { get; }

    /// <summary>Lease path for one participant process.</summary>
    string LeasePath(int processId, long startTicksUtc);

    /// <summary>Glob matching every lease belonging to this user + session.</summary>
    string LeaseSearchPattern { get; }

    /// <summary>Parses a lease file name back into the owning process identity.</summary>
    bool TryParseLeaseFileName(string fileName, out int processId, out long startTicksUtc);

    /// <summary>Creates the lock and participants directories, restricted to the current user.</summary>
    void EnsureDirectories();
}

/// <inheritdoc cref="IInteractiveDesktopPaths"/>
internal sealed class InteractiveDesktopPaths : IInteractiveDesktopPaths
{
    /// <summary>
    /// Redirects the whole coordination file set. Exists so multiprocess and file-level tests never
    /// touch the developer's live desktop coordination state. It relocates coordination; it never
    /// disables it, so a test still exercises the real locking protocol.
    /// </summary>
    internal const string LockDirectoryOverrideVariable = "WINAPP_UI_LOCK_DIRECTORY";

    private const string FilePrefix = "interactive-desktop-";
    private const string LeaseExtension = ".lease";

    private readonly string _sessionToken;
    private bool _directoriesVerified;

    public InteractiveDesktopPaths(IProcessInspector processInspector)
    {
        _sessionToken = processInspector.CurrentSessionId.ToString(CultureInfo.InvariantCulture);
        LockDirectory = ResolveLockDirectory();
        ParticipantsDirectory = Path.Combine(LockDirectory, "participants");
        StateLockPath = Path.Combine(LockDirectory, $"{FilePrefix}{_sessionToken}.state.lock");
        StatePath = Path.Combine(LockDirectory, $"{FilePrefix}{_sessionToken}.state.json");
        ActiveLockPath = Path.Combine(LockDirectory, $"{FilePrefix}{_sessionToken}.active.lock");
        LeaseSearchPattern = $"{FilePrefix}{_sessionToken}-*{LeaseExtension}";
    }

    public string LockDirectory { get; }

    public string ParticipantsDirectory { get; }

    public string StateLockPath { get; }

    public string StatePath { get; }

    public string ActiveLockPath { get; }

    public string LeaseSearchPattern { get; }

    public string LeasePath(int processId, long startTicksUtc)
        => Path.Combine(
            ParticipantsDirectory,
            $"{FilePrefix}{_sessionToken}-{processId.ToString(CultureInfo.InvariantCulture)}-" +
            $"{ProcessInspector.FormatStartTicks(startTicksUtc)}{LeaseExtension}");

    public bool TryParseLeaseFileName(string fileName, out int processId, out long startTicksUtc)
    {
        processId = 0;
        startTicksUtc = 0;

        var expectedPrefix = $"{FilePrefix}{_sessionToken}-";
        if (!fileName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(LeaseExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var body = fileName[expectedPrefix.Length..^LeaseExtension.Length];
        var separator = body.LastIndexOf('-');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return false;
        }

        return int.TryParse(body[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            && long.TryParse(body[(separator + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out startTicksUtc);
    }

    public void EnsureDirectories()
    {
        // Verified once per process: the check is a DACL read per directory, and every state-lock
        // acquisition and lease open calls this.
        if (_directoriesVerified)
        {
            return;
        }

        EnsureRestrictedDirectory(LockDirectory);
        EnsureRestrictedDirectory(ParticipantsDirectory);
        _directoriesVerified = true;
    }

    private static string ResolveLockDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(LockDirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ValidateLockDirectory(overridePath.Trim(), LockDirectoryOverrideVariable);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                "The local application data folder could not be resolved, so UI turn coordination has nowhere to store its state.",
                "Ensure LOCALAPPDATA is set for this user, or set WINAPP_UI_LOCK_DIRECTORY to a fully qualified local directory.");
        }

        return ValidateLockDirectory(
            Path.Combine(localAppData, "Microsoft", "WinAppCli", "locks"),
            "LOCALAPPDATA");
    }

    private static string ValidateLockDirectory(string path, string source)
    {
        // A relative path would resolve against the caller's working directory, so two processes in
        // different directories would coordinate against different files and silently not cooperate.
        if (!Path.IsPathFullyQualified(path))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination directory resolved from {source} is not a fully qualified path.",
                "Set WINAPP_UI_LOCK_DIRECTORY to a fully qualified local directory such as C:\\Temp\\winapp-locks.");
        }

        // Byte-range locking over SMB is advisory and unreliable for the exclusive-share protocol this
        // coordinator depends on, so a network path would produce silent overlap instead of exclusion.
        if (PathSafety.IsNetworkPath(path))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination directory resolved from {source} is a network path, which cannot provide reliable exclusive file locks.",
                "Set WINAPP_UI_LOCK_DIRECTORY to a fully qualified path on a local drive.");
        }

        return Path.GetFullPath(path);
    }

    /// <remarks>
    /// The parent (<c>%LOCALAPPDATA%</c>) is already restricted to the current user, but that is not
    /// enough on its own: <c>WINAPP_UI_LOCK_DIRECTORY</c> can point at a shared location such as
    /// <c>C:\Temp</c>, and a directory created by an earlier run may have inherited permissive rules.
    /// Coordination state is not a secret, but a foreign writer could corrupt it or hold a lease and
    /// stall this user's UI workflows indefinitely, so an existing directory is inspected and repaired
    /// rather than trusted.
    /// </remarks>
    private static void EnsureRestrictedDirectory(string path)
    {
        var directoryInfo = new DirectoryInfo(path);

        if (!directoryInfo.Exists)
        {
            try
            {
                directoryInfo.Create(BuildCurrentUserOnlySecurity());
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Unavailable(path, ex);
            }
            catch (IOException ex)
            {
                // Another winapp process can win the create race; that is success, not failure. Fall
                // through so the existing directory still gets its DACL verified below.
                directoryInfo.Refresh();
                if (!directoryInfo.Exists)
                {
                    throw Unavailable(path, ex);
                }
            }
        }

        RepairAccessRulesIfNeeded(directoryInfo);
    }

    /// <summary>
    /// Re-applies the current-user-only DACL when the existing one is inherited or grants any other
    /// identity. A no-op in the overwhelmingly common case, so the per-process check stays cheap.
    /// </summary>
    private static void RepairAccessRulesIfNeeded(DirectoryInfo directoryInfo)
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null)
        {
            // Without an identity there is nothing to scope the DACL to; inherited parent permissions
            // are the best available protection.
            return;
        }

        try
        {
            var existing = directoryInfo.GetAccessControl();
            if (IsCurrentUserOnly(existing, currentUser))
            {
                return;
            }

            directoryInfo.SetAccessControl(BuildCurrentUserOnlySecurity());

            // Verify rather than assume. Taking ownership can be refused without throwing on every
            // Windows configuration, and a directory whose owner is still a stranger remains
            // re-permissionable behind our back — so confirm the repair actually took effect and fail
            // closed if it did not.
            directoryInfo.Refresh();
            if (!IsCurrentUserOnly(directoryInfo.GetAccessControl(), currentUser))
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    $"The UI coordination directory '{directoryInfo.FullName}' is still owned or reachable by another user after repair.",
                    "Point WINAPP_UI_LOCK_DIRECTORY at a directory this user owns, or remove the override to use the default location under %LOCALAPPDATA%.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            // The directory is reachable but cannot be secured — for example it belongs to another user.
            // Coordinating through storage a third party can tamper with is worse than not running.
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination directory '{directoryInfo.FullName}' could not be restricted to the current user: {ex.Message}",
                "Point WINAPP_UI_LOCK_DIRECTORY at a directory this user owns, or remove the override to use the default location under %LOCALAPPDATA%.");
        }
    }

    /// <summary>
    /// Whether <paramref name="security"/> describes a directory only the current user can reach or
    /// re-permission.
    /// </summary>
    /// <remarks>
    /// Owner is checked as well as the DACL because the owner of an object implicitly holds
    /// <c>WRITE_DAC</c>: a foreign owner can rewrite even a protected, current-user-only DACL and grant
    /// itself access at any time. That matters most for a <c>WINAPP_UI_LOCK_DIRECTORY</c> override under
    /// a shared path, where another user may have created the directory first.
    /// </remarks>
    internal static bool IsCurrentUserOnly(DirectorySecurity security, SecurityIdentifier currentUser)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || owner != currentUser)
        {
            return false;
        }

        if (!security.AreAccessRulesProtected)
        {
            // Inherited rules can grant anyone the parent grants, which for a shared override directory
            // includes other users.
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || sid != currentUser)
            {
                return false;
            }
        }

        return true;
    }

    private static DirectorySecurity BuildCurrentUserOnlySecurity()
    {
        var security = new DirectorySecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static UiCoordinationException Unavailable(string path, Exception ex)
        => new(
            UiCoordinationErrorCodes.Unavailable,
            $"The UI coordination directory '{path}' could not be created: {ex.Message}",
            "Check that the current user can write to the directory, or set WINAPP_UI_LOCK_DIRECTORY to a writable local directory.");
}

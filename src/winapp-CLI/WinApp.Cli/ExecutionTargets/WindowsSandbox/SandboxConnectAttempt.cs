// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// Proof of which host process winapp asked to open a Sandbox client window.
/// </summary>
/// <remarks>
/// Windows Sandbox creates <c>WindowsSandboxRemoteSession.exe</c> as a direct child of the
/// <c>wsb connect</c> process that asked for it. That parentage is what makes ownership provable:
/// a client whose parent is this launcher is the one winapp asked for, and a client whose parent is
/// anything else belongs to another caller — a distinction that holds however the two connects
/// happen to be interleaved.
/// </remarks>
/// <param name="LauncherProcessId">
/// The <c>wsb connect</c> process winapp started. Valid only while the attempt that produced it is
/// undisposed, which is what keeps Windows from recycling the number underneath the comparison.
/// </param>
internal sealed record SandboxConnectOwnership(int LauncherProcessId);

/// <summary>
/// A <c>wsb connect</c> winapp started, held open long enough to identify the window it created.
/// </summary>
/// <remarks>
/// This exists to own a process handle, not to control the client. Windows only guarantees a process
/// ID is not reused while some handle to it is open, so the handle is kept until the caller has
/// finished matching client windows against <see cref="Ownership"/> — releasing it earlier would
/// reintroduce exactly the mistaken-identity problem the parent ID is there to prevent.
/// <para>
/// Disposing does not stop the client: the connect process is deliberately never waited on and never
/// killed, because it is the user's interactive Sandbox window.
/// </para>
/// </remarks>
internal sealed class SandboxConnectAttempt : IDisposable
{
    private readonly Process? _launcher;

    private SandboxConnectAttempt(Process? launcher, SandboxConnectOwnership? ownership)
    {
        _launcher = launcher;
        Ownership = ownership;
    }

    /// <summary>An attempt whose launcher Windows would not identify.</summary>
    /// <remarks>
    /// Not a failure. The client may well be starting; winapp simply has no evidence to tie a window
    /// to this connect, and says so rather than claiming one.
    /// </remarks>
    public static SandboxConnectAttempt Unidentified { get; } = new(null, null);

    /// <summary>The launcher to attribute new client windows to, or null when there is none.</summary>
    public SandboxConnectOwnership? Ownership { get; }

    /// <summary>Wraps a launched connect process, keeping its ID reserved.</summary>
    public static SandboxConnectAttempt From(Process launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);

        return new SandboxConnectAttempt(launcher, new SandboxConnectOwnership(launcher.Id));
    }

    /// <summary>Names a launcher by ID alone, for tests that have no process to launch.</summary>
    internal static SandboxConnectAttempt ForLauncher(int launcherProcessId) =>
        new(null, new SandboxConnectOwnership(launcherProcessId));

    /// <inheritdoc/>
    public void Dispose() => _launcher?.Dispose();
}

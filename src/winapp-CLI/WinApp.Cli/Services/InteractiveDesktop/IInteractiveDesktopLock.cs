// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Scope around a desktop-sensitive section — foreground, focus, cursor, <c>SendInput</c>, synthetic
/// pointer input, window restore, or live-screen capture. Entering takes <c>active.lock</c>; disposing
/// releases it.
/// </summary>
/// <remarks>
/// Passed to services (screenshot, record) as an explicit parameter rather than read from ambient
/// state, matching the repo's existing injectable-seam style and letting unit tests substitute a no-op.
/// </remarks>
internal interface IDesktopSection
{
    /// <summary>
    /// Acquires <c>active.lock</c> for the duration of the returned scope. Reentrant within a process:
    /// a nested enter increments a refcount and releases the lock only when the outermost scope closes.
    /// </summary>
    Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken);
}

/// <summary>The workflow turn a coordinated command is executing under.</summary>
internal interface IUiTurn : IDesktopSection
{
    /// <summary>The mode this command was admitted with, after any escalation.</summary>
    UiTurnMode Mode { get; }

    /// <summary>Milliseconds spent queued before execution began. Zero when the turn was free.</summary>
    long WaitedMs { get; }

    /// <summary>
    /// Converts an in-flight <see cref="UiTurnMode.Observe"/> command into a
    /// <see cref="UiTurnMode.DesktopExclusive"/> one and waits for the barrier (spec §6.5). Used by
    /// <c>ui screenshot</c> when a target turns out to need restore or foreground: the invocation
    /// discards its buffered captures, escalates as a whole, and recaptures from the beginning.
    /// </summary>
    Task EscalateToDesktopExclusiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Cooperative desktop turn coordination across concurrent <c>winapp.exe</c> processes (issue #764).
/// </summary>
internal interface IInteractiveDesktopLock
{
    /// <summary>
    /// Runs <paramref name="body"/> under the workflow turn implied by <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// Callers must have completed every local validation before calling this: a malformed command must
    /// never open a lease, take a ticket, or join an indefinite queue (spec §10). The forward barrier
    /// wraps <paramref name="body"/>; <c>active.lock</c> is <em>not</em> held across it — the body takes
    /// it only for its desktop-sensitive section via <see cref="IDesktopSection.EnterAsync"/>.
    /// </remarks>
    /// <param name="mode">The command's coordination mode.</param>
    /// <param name="operation">Command name for diagnostics, e.g. <c>ui click</c>. Never arguments.</param>
    /// <param name="parseResult">Used only to resolve <c>--json</c> / <c>--verbose</c> / <c>--quiet</c>.</param>
    /// <param name="body">The command's work, which contacts the target app.</param>
    /// <returns>
    /// The body's exit code, or <c>130</c> when the command was cancelled while queued and never ran.
    /// </returns>
    Task<int> RunCoordinatedAsync(
        UiTurnMode mode,
        string operation,
        ParseResult parseResult,
        Func<IUiTurn, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken);
}

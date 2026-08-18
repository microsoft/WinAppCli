// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pass-through <see cref="IInteractiveDesktopLock"/> for command handler tests.
/// </summary>
/// <remarks>
/// Command tests build the real DI graph, so without this fake every <c>ui</c> test would open lock
/// files under the developer's real <c>%LOCALAPPDATA%</c> and queue against their live desktop
/// workflows. It also records what each command asked for, which is how the tests assert coordination
/// mode, desktop-section placement, and — critically — that a command rejected in preflight never
/// touched coordination at all.
/// </remarks>
internal sealed class FakeInteractiveDesktopLock : IInteractiveDesktopLock
{
    /// <summary>Every coordinated run, in order. Empty means preflight rejected before coordination.</summary>
    public List<(UiTurnMode Mode, string Operation)> Runs { get; } = [];

    /// <summary>How many times a body entered a desktop-sensitive section.</summary>
    public int DesktopSectionEnters { get; private set; }

    /// <summary>How many desktop sections are open right now; tests assert this around observable calls.</summary>
    public int OpenDesktopSections { get; private set; }

    /// <summary>How many times a body escalated an observation to <c>DesktopExclusive</c>.</summary>
    public int Escalations { get; private set; }

    /// <summary>Set to throw from <see cref="RunCoordinatedAsync"/>, to cover coordination failures.</summary>
    public UiCoordinationException? ThrowOnRun { get; set; }

    /// <summary>
    /// Set to throw from <c>EscalateToDesktopExclusiveAsync</c>, covering an escalation that is
    /// cancelled while queued or refused because coordination is unavailable. Handlers must let these
    /// escape rather than flattening them into <c>internal_error</c>.
    /// </summary>
    public Exception? ThrowOnEscalation { get; set; }

    /// <summary>Milliseconds reported as queue wait, so output/telemetry paths can be exercised.</summary>
    public long WaitedMs { get; set; }

    public Task<int> RunCoordinatedAsync(
        UiTurnMode mode,
        string operation,
        ParseResult parseResult,
        Func<IUiTurn, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        Runs.Add((mode, operation));

        if (ThrowOnRun is { } failure)
        {
            throw failure;
        }

        return body(new FakeTurn(this, mode), cancellationToken);
    }

    private sealed class FakeTurn(FakeInteractiveDesktopLock owner, UiTurnMode mode) : IUiTurn
    {
        public UiTurnMode Mode { get; private set; } = mode;

        public long WaitedMs => owner.WaitedMs;

        public Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            owner.DesktopSectionEnters++;
            owner.OpenDesktopSections++;
            return Task.FromResult<IAsyncDisposable>(new FakeSection(owner));
        }

        public Task EscalateToDesktopExclusiveAsync(CancellationToken cancellationToken)
        {
            owner.Escalations++;

            if (owner.ThrowOnEscalation is { } failure)
            {
                return Task.FromException(failure);
            }

            Mode = UiTurnMode.DesktopExclusive;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSection(FakeInteractiveDesktopLock owner) : IAsyncDisposable
    {
        private bool _disposed;

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.OpenDesktopSections--;
            }

            return ValueTask.CompletedTask;
        }
    }
}

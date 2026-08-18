// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// An <see cref="IDesktopSection"/> that grants the section immediately without taking
/// <c>active.lock</c>.
/// </summary>
/// <remarks>
/// Only for callers that provably have no turn to coordinate under: unit tests exercising capture
/// mechanics directly against <see cref="UiAutomationService"/>, and gated real-UIA tests that drive
/// the service outside the command pipeline. Command handlers always pass their real
/// <see cref="IUiTurn"/> — using this there would silently opt a command out of coordination.
/// </remarks>
internal sealed class NullDesktopSection : IDesktopSection
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static NullDesktopSection Instance { get; } = new();

    private NullDesktopSection()
    {
    }

    public Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
        => Task.FromResult<IAsyncDisposable>(NoOpScope.Instance);

    private sealed class NoOpScope : IAsyncDisposable
    {
        public static NoOpScope Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

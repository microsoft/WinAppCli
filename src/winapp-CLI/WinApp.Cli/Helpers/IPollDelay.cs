// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// The wait-between-polls delay used by <c>winapp ui wait-for</c>. Extracted behind an interface so
/// the retry loop's "condition not met yet — keep polling" continuations are unit-testable without a
/// real wall-clock wait: production wires <see cref="RealPollDelay"/> (a plain <see cref="Task.Delay"/>),
/// while tests inject a fast fake so the poll continuations run deterministically.
/// </summary>
internal interface IPollDelay
{
    /// <summary>Wait <paramref name="milliseconds"/> before the next poll, honoring cancellation.</summary>
    Task DelayAsync(int milliseconds, CancellationToken ct);
}

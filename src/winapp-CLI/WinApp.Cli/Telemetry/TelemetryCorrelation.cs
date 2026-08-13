// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Telemetry;

/// <summary>
/// Holds a per-invocation correlation id in an <see cref="AsyncLocal{T}"/> so the generic
/// command lifecycle events (CommandInvoked/CommandCompleted) and any command-specific event a
/// handler emits during the same invocation share one <c>relatedActivityId</c>. A backend can then
/// group all three into a single logical run — finer-grained than the process-wide activity id,
/// which is useful if the correlation model ever needs to distinguish nested invocations.
/// </summary>
internal static class TelemetryCorrelation
{
    private static readonly AsyncLocal<Guid> Id = new();

    /// <summary>The current invocation's correlation id, or <see cref="Guid.Empty"/> when none is active.</summary>
    public static Guid CurrentId => Id.Value;

    /// <summary>Starts a new correlation scope for the current async flow and returns its id.</summary>
    public static Guid Begin()
    {
        var id = Guid.NewGuid();
        Id.Value = id;
        return id;
    }
}

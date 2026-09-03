// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// The persisted coordination state shared by every <c>winapp.exe</c> in one Windows session
/// (<c>interactive-desktop-{session}.state.json</c>, spec §8). Every read and write happens while
/// holding <c>state.lock</c>; publication is atomic, so a crash leaves either the old complete state
/// or the new complete state.
/// </summary>
/// <remarks>
/// Unknown properties are round-tripped through <see cref="ExtensionData"/> so a newer binary's
/// additive fields survive a rewrite by an older compatible binary (spec §8, "Preserve unknown fields
/// when rewriting a known version"). A <see cref="Version"/> greater than
/// <see cref="CurrentVersion"/> is never reset or downgraded.
/// </remarks>
internal sealed class InteractiveDesktopState
{
    /// <summary>The schema version this binary reads and writes.</summary>
    internal const int CurrentVersion = 1;

    /// <summary>Schema version. Values above <see cref="CurrentVersion"/> are failed closed, never rewritten.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Incremented every time the turn is claimed by an owner. Diagnostic and test observability.</summary>
    public long TurnId { get; set; }

    /// <summary>
    /// Monotonic tick at which the current turn was claimed, written alongside every
    /// <see cref="TurnId"/> increment. Lets any participant report how long the <em>workflow</em> turn
    /// has been held — across all of that owner's commands — rather than how long its own command
    /// waited (spec §16 turn-age bucket). Zero when no owner holds the turn.
    /// </summary>
    public long TurnStartedTick64 { get; set; }

    /// <summary>Next globally monotonic arrival ticket. Tickets order the barrier and the global FIFO.</summary>
    public long NextTicket { get; set; } = 1;

    /// <summary>The owner currently holding the turn, or <see langword="null"/> when the desktop is free.</summary>
    public OwnerRecord? Owner { get; set; }

    /// <summary>
    /// Monotonic deadline after which an idle turn may be handed off. Only consulted when
    /// <see cref="OwnerCommands"/> is empty — a waiting or running owner command keeps the turn
    /// regardless of this value.
    /// </summary>
    public long IdleExpiresTick64 { get; set; }

    /// <summary>Human-readable mirror of <see cref="IdleExpiresTick64"/>. Diagnostic only; never compared.</summary>
    public string? DiagnosticIdleExpiresUtc { get; set; }

    /// <summary>Commands belonging to the current owner, in arrival order.</summary>
    public List<OwnerCommandEntry> OwnerCommands { get; set; } = [];

    /// <summary>Other owners' commands waiting for the turn, oldest ticket first.</summary>
    public List<WaiterEntry> Waiters { get; set; } = [];

    /// <summary>Unknown properties from a newer writer, preserved verbatim on rewrite.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>The state a coordinator starts from when no file exists or a corrupt file was quarantined.</summary>
    public static InteractiveDesktopState CreateFresh() => new()
    {
        Version = CurrentVersion,
        TurnId = 0,
        TurnStartedTick64 = 0,
        NextTicket = 1,
        Owner = null,
        IdleExpiresTick64 = 0,
        OwnerCommands = [],
        Waiters = [],
    };

    /// <summary>Allocates the next arrival ticket and advances the counter.</summary>
    public long AllocateTicket()
    {
        var ticket = NextTicket;
        NextTicket = ticket + 1;
        return ticket;
    }
}

/// <summary>The owner currently holding the turn.</summary>
internal sealed class OwnerRecord
{
    /// <summary>How this owner was resolved. Drives whether the turn earns a post-command idle grace.</summary>
    public UiOwnerKind Kind { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the domain-separated owner payload. Never the raw
    /// <c>WINAPP_UI_WORKFLOW_ID</c>, and never emitted in output, logs or telemetry.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Unknown properties from a newer writer, preserved verbatim on rewrite.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>One command belonging to the current owner (spec §8).</summary>
internal sealed class OwnerCommandEntry
{
    /// <summary>
    /// Globally monotonic arrival ticket. Present for every <see cref="UiTurnMode.TurnShared"/> and
    /// <see cref="UiTurnMode.DesktopExclusive"/> command; <see langword="null"/> for
    /// <see cref="UiTurnMode.Observe"/>, which never serializes as a barrier. An observation that
    /// escalates is assigned a ticket at escalation time.
    /// </summary>
    public long? Ticket { get; set; }

    /// <summary>Owning <c>winapp.exe</c> process id.</summary>
    public int Pid { get; set; }

    /// <summary>
    /// The owning process's <c>Process.StartTime.ToUniversalTime().Ticks</c>. Combined with
    /// <see cref="Pid"/> this identifies the participant lease and detects PID reuse.
    /// </summary>
    public long ProcessStartTicksUtc { get; set; }

    /// <summary>Command name for diagnostics, e.g. <c>ui click</c>. Never includes arguments.</summary>
    public string Operation { get; set; } = "";

    /// <summary>The command's coordination mode.</summary>
    public UiTurnMode Mode { get; set; }

    /// <summary>Whether the command is blocked behind an earlier barrier or executing.</summary>
    public UiCommandStatus Status { get; set; }

    /// <summary>Unknown properties from a newer writer, preserved verbatim on rewrite.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A command from another owner waiting for the current turn (spec §8).</summary>
internal sealed class WaiterEntry
{
    /// <summary>Globally monotonic arrival ticket. Defines strict FIFO order among waiters.</summary>
    public long Ticket { get; set; }

    /// <summary>The waiting command's owner key (SHA-256 hex). Becomes the current owner on promotion.</summary>
    public string OwnerKey { get; set; } = "";

    /// <summary>Owning <c>winapp.exe</c> process id.</summary>
    public int Pid { get; set; }

    /// <summary>The owning process's <c>Process.StartTime.ToUniversalTime().Ticks</c>.</summary>
    public long ProcessStartTicksUtc { get; set; }

    /// <summary>The owner kind to install when this waiter is promoted.</summary>
    public UiOwnerKind OwnerKind { get; set; }

    /// <summary>Command name for diagnostics, e.g. <c>ui click</c>. Never includes arguments.</summary>
    public string Operation { get; set; } = "";

    /// <summary>
    /// The mode this waiter requested, stored so any process can promote it without inferring behavior
    /// from the operation name (spec §8).
    /// </summary>
    public UiTurnMode Mode { get; set; }

    /// <summary>Unknown properties from a newer writer, preserved verbatim on rewrite.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

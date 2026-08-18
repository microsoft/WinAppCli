// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// How a <c>winapp ui</c> command participates in cooperative desktop turns (issue #764).
/// </summary>
/// <remarks>
/// Windows exposes a single foreground window, keyboard focus, cursor and <c>SendInput</c> stream, so
/// concurrent <c>winapp.exe</c> processes can dismiss each other's transient UI even when they target
/// different apps. Every UI command declares one of these modes; the coordinator uses it to decide
/// whether the command claims the workflow turn, waits behind a forward barrier, or runs concurrently.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<UiTurnMode>))]
internal enum UiTurnMode
{
    /// <summary>
    /// Does not claim a free turn. A non-owner runs immediately and detached (no lease, no queue entry);
    /// the current owner registers so the observation pins and renews that owner's turn while it reads
    /// transient UI.
    /// </summary>
    Observe,

    /// <summary>
    /// Claims or waits for the workflow turn. Several same-owner <c>TurnShared</c> commands may overlap
    /// unless an earlier <see cref="DesktopExclusive"/> forward barrier is waiting or running. Used by
    /// <c>ui record</c>, which pins the owner for the whole capture while same-owner input continues.
    /// </summary>
    TurnShared,

    /// <summary>
    /// Claims or waits for the workflow turn, creates a forward barrier in the owner's command stream,
    /// and takes <c>active.lock</c> for its desktop-sensitive section (foreground, focus, cursor,
    /// <c>SendInput</c>, synthetic pointer input, restore, live-screen capture).
    /// </summary>
    DesktopExclusive,
}

/// <summary>How the logical workflow owner behind a command was resolved (spec §5).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<UiOwnerKind>))]
internal enum UiOwnerKind
{
    /// <summary>Resolved from <c>WINAPP_UI_OWNER_ID</c>. Groups cooperating processes explicitly.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("explicit")]
    Explicit,

    /// <summary>
    /// Derived from the immediate parent PID plus that parent's start time, which groups the commands of
    /// one long-lived shell or script. Never walks farther up the tree — a higher ancestor may be shared
    /// by unrelated workflows.
    /// </summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("parent")]
    Parent,

    /// <summary>
    /// A unique one-command owner used when parent inspection fails. It queues normally but receives no
    /// post-command idle grace.
    /// </summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("anonymous")]
    Anonymous,
}

/// <summary>Whether a registered owner command is waiting behind the barrier or currently executing.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<UiCommandStatus>))]
internal enum UiCommandStatus
{
    /// <summary>Admitted with an arrival ticket but blocked by an earlier <see cref="UiTurnMode.DesktopExclusive"/> command.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("waiting")]
    Waiting,

    /// <summary>Eligible and executing. Counts as owner activity, so it blocks handoff to another owner.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("running")]
    Running,
}

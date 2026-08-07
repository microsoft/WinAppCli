// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;

namespace WinApp.Cli.Telemetry.Events;

/// <summary>
/// Usage telemetry for <c>winapp find-ui</c>. Emitted by the command handler
/// (not the generic <see cref="CommandInvokedEvent"/> path) because the fields
/// that make this useful — which scenario ids actually resolved — can only be
/// validated against the loaded corpus, which the parse-time generic path never
/// sees. This is a deliberate, reviewer-flagged deviation from the "commands do
/// not emit bespoke telemetry" convention: id-against-dataset validation forces
/// the feature to own the emission.
///
/// <para><b>Privacy — allow-list, not blanket redaction.</b> Only values from a
/// bounded, non-PII vocabulary are emitted:
/// <list type="bullet">
///   <item><see cref="Source"/> — the <c>--source</c> value, already validated
///   against the provider registry before the handler reaches here, so it is
///   always one of gallery/toolkit/reactor/core (or null).</item>
///   <item><see cref="ResolvedIds"/> — only the corpus-canonical scenario ids
///   that the requested <c>--id</c> values resolved to (e.g.
///   <c>gallery-tabview-1</c>), never the caller's raw token. Requested ids that
///   do NOT resolve are never emitted as strings — they are only counted in
///   <see cref="UnresolvedIdCount"/>, so an arbitrary/typo'd id can't leak.</item>
/// </list>
/// The free-form <c>query</c> argument is intentionally NOT captured on this
/// event — a user can type anything into it, so it stays out of telemetry (the
/// generic <see cref="CommandInvokedEvent"/> already redacts it to
/// <c>[string]</c>).</para>
///
/// <para>Logged at <see cref="LogLevel.Measure"/> — this is new usage-measure
/// telemetry that has not been through Critical-event approval; the telemetry
/// owner may re-level it to Critical once approved.</para>
/// </summary>
[EventData]
internal class FindUiUsageEvent : EventBase
{
    private FindUiUsageEvent(
        string mode,
        string? source,
        bool includeReactor,
        bool json,
        int resultCount,
        int requestedIdCount,
        int resolvedIdCount,
        string? resolvedIds)
    {
        Mode = mode;
        Source = source;
        IncludeReactor = includeReactor;
        Json = json;
        ResultCount = resultCount;
        RequestedIdCount = requestedIdCount;
        ResolvedIdCount = resolvedIdCount;
        UnresolvedIdCount = requestedIdCount - resolvedIdCount;
        ResolvedIds = resolvedIds;
    }

    /// <summary>The invocation mode: <c>"search"</c>, <c>"fetch"</c>, or <c>"list"</c>.</summary>
    public string Mode { get; private set; }

    /// <summary>The registry-validated <c>--source</c> value
    /// (gallery/toolkit/reactor/core) for a search, or null. Never a free-form
    /// string — invalid values are rejected before the handler emits.</summary>
    public string? Source { get; private set; }

    /// <summary>Whether the opt-in Reactor source was included this invocation.</summary>
    public bool IncludeReactor { get; }

    /// <summary>Whether <c>--json</c> output was requested.</summary>
    public bool Json { get; }

    /// <summary>Result count: matched controls for a search, listed ids for a
    /// browse, 0 for a fetch.</summary>
    public int ResultCount { get; }

    /// <summary>Fetch mode: number of <c>--id</c> values requested.</summary>
    public int RequestedIdCount { get; }

    /// <summary>Fetch mode: how many requested ids resolved to a real corpus scenario.</summary>
    public int ResolvedIdCount { get; }

    /// <summary>Fetch mode: how many requested ids did NOT resolve (counted, never
    /// emitted as strings, so a typo'd/arbitrary id can't leak).</summary>
    public int UnresolvedIdCount { get; }

    /// <summary>Fetch mode: the comma-joined, corpus-canonical scenario ids that
    /// the requested ids resolved to (bounded, non-PII), or null when none
    /// resolved / not a fetch.</summary>
    public string? ResolvedIds { get; private set; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServiceUsage;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        // These fields are drawn from a bounded, corpus-controlled vocabulary and
        // carry no PII, but run the sanitizer over every string field anyway to
        // honor the EventBase contract (defensive, cheap).
        Mode = replaceSensitiveStrings(Mode);
        if (Source is not null)
        {
            Source = replaceSensitiveStrings(Source);
        }
        if (ResolvedIds is not null)
        {
            ResolvedIds = replaceSensitiveStrings(ResolvedIds);
        }
    }

    /// <summary>Emit a search-mode usage event. <paramref name="source"/> must be
    /// a registry-validated value or null (never free-form).</summary>
    public static void LogSearch(string? source, bool includeReactor, bool json, int matchCount) =>
        Emit(CreateSearch(source, includeReactor, json, matchCount));

    /// <summary>Emit a fetch-mode usage event. <paramref name="resolvedIds"/> must
    /// contain only ids that resolved to a real corpus scenario; unresolved ids are
    /// reflected only in the count (<paramref name="requestedIdCount"/> minus the
    /// resolved count), never as strings.</summary>
    public static void LogFetch(bool includeReactor, bool json, IReadOnlyCollection<string> resolvedIds, int requestedIdCount) =>
        Emit(CreateFetch(includeReactor, json, resolvedIds, requestedIdCount));

    /// <summary>Emit a list/browse-mode usage event.</summary>
    public static void LogList(bool json, int count) =>
        Emit(CreateList(json, count));

    // Internal factories so tests can assert the arg→field mapping directly without
    // reconstructing the payload from ETW (the constructor stays private).
    internal static FindUiUsageEvent CreateSearch(string? source, bool includeReactor, bool json, int matchCount) =>
        new("search", source, includeReactor, json, matchCount, 0, 0, null);

    internal static FindUiUsageEvent CreateFetch(bool includeReactor, bool json, IReadOnlyCollection<string> resolvedIds, int requestedIdCount) =>
        new(
            "fetch",
            source: null,
            includeReactor,
            json,
            resultCount: 0,
            requestedIdCount,
            resolvedIdCount: resolvedIds.Count,
            resolvedIds: resolvedIds.Count > 0 ? string.Join(",", resolvedIds) : null);

    internal static FindUiUsageEvent CreateList(bool json, int count) =>
        new("list", source: null, includeReactor: false, json, count, 0, 0, null);

    private static void Emit(FindUiUsageEvent usageEvent) =>
        TelemetryFactory.Get<ITelemetry>().Log("FindUiUsage_Event", LogLevel.Measure, usageEvent);
}

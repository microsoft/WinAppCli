// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;

namespace WinApp.Cli.Telemetry.Events;

/// <summary>
/// Command-specific telemetry for <c>winapp new</c>. Complements the generic
/// CommandInvoked/CommandCompleted events (whose string options are redacted) by capturing the
/// low-cardinality, non-PII shape of the invocation: which template short name was resolved, how
/// the version was requested, whether the run was interactive, and how it ended. Correlated to the
/// generic events via <c>relatedActivityId</c>.
/// </summary>
[EventData]
internal class NewCommandEvent : EventBase
{
    internal NewCommandEvent(string? template, bool templateIsItem, string versionMode, bool interactive, bool listOnly, string outcome, int exitCode)
    {
        Template = template;
        TemplateIsItem = templateIsItem;
        VersionMode = versionMode;
        Interactive = interactive;
        ListOnly = listOnly;
        Outcome = outcome;
        ExitCode = exitCode;
    }

    /// <summary>Resolved template short name (e.g. <c>winui-navview</c>), or null when none was resolved (e.g. <c>--list</c>).</summary>
    public string? Template { get; private set; }

    /// <summary>True when the resolved template is an item template rather than a project template.</summary>
    public bool TemplateIsItem { get; private set; }

    /// <summary>How the caller asked us to resolve the pack version: Default, Latest, Installed, or Explicit.</summary>
    public string VersionMode { get; private set; }

    /// <summary>True when the run prompted interactively (not <c>--yes</c>/<c>--json</c>/non-interactive host).</summary>
    public bool Interactive { get; private set; }

    /// <summary>True when invoked as <c>--list</c> (enumerate templates only, no scaffold).</summary>
    public bool ListOnly { get; private set; }

    /// <summary>How the invocation ended (e.g. created, listed, invalid-args, sdk-missing, pack-failed, scaffold-failed, cancelled, error).</summary>
    public string Outcome { get; private set; }

    /// <summary>Process exit code for the invocation.</summary>
    public int ExitCode { get; private set; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServiceUsage;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        if (Template is not null)
        {
            Template = replaceSensitiveStrings(Template);
        }

        VersionMode = replaceSensitiveStrings(VersionMode);
        Outcome = replaceSensitiveStrings(Outcome);
    }

    public static void Log(string? template, bool templateIsItem, string versionMode, bool interactive, bool listOnly, string outcome, int exitCode, Guid relatedActivityId)
    {
        TelemetryFactory.Get<ITelemetry>().Log(
            "NewCommand_Event",
            LogLevel.Critical,
            new NewCommandEvent(template, templateIsItem, versionMode, interactive, listOnly, outcome, exitCode),
            relatedActivityId);
    }
}

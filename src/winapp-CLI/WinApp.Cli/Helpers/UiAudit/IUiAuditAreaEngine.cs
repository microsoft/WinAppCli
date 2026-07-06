// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers.UiAudit;

/// <summary>
/// One independent audit area. Implementations evaluate a single <see cref="Area"/> over the shared
/// <see cref="UiAuditContext"/> and return only their own findings; the
/// <see cref="UiAuditOrchestrator"/> merges results across areas. This is the core extension point:
/// adding a new engine (and registering it in DI) adds a new <c>--area</c> with no changes to the
/// orchestrator or command.
/// </summary>
internal interface IUiAuditAreaEngine
{
    /// <summary>The <see cref="AuditArea"/> name this engine implements.</summary>
    string Area { get; }

    /// <summary>
    /// <c>true</c> when this area needs measured contrast ratios, so the orchestrator can lazily
    /// perform the (relatively expensive) window pixel capture only when required.
    /// </summary>
    bool RequiresContrastCapture { get; }

    /// <summary>Evaluate this area and return its findings (never <c>null</c>).</summary>
    UiAuditResult Evaluate(UiAuditContext context);
}

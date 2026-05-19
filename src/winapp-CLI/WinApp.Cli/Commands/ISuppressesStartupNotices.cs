// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

/// <summary>
/// Marker interface for commands whose stdout is intended to be machine- or
/// agent-consumed (e.g., pure markdown, JSON, or other structured output) and
/// which must NOT be prefixed by interactive UI such as the first-run notice
/// or update banner.
///
/// Apply this to the top-level command (or any subcommand) whose output should
/// stay clean. Program.cs walks the resolved command + its ancestors looking
/// for this interface before showing the first-run / update notices.
/// </summary>
internal interface ISuppressesStartupNotices
{
}

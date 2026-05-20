// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Centralised <see cref="ParserConfiguration"/> used for every winapp command-line parse.
/// </summary>
/// <remarks>
/// POSIX bundling is disabled because winapp uses a Windows-style CLI that does not advertise
/// short-option clustering (e.g. <c>-abc</c>) or attached-value short options (e.g. <c>-w7932630</c>),
/// and the silent reinterpretation of <c>-app</c> as <c>-a pp</c> is a usability trap (issue #467).
/// </remarks>
internal static class WinAppParserConfiguration
{
    public static ParserConfiguration Default { get; } = new()
    {
        EnablePosixBundling = false
    };
}

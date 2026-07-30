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
/// <para>
/// Response-file token expansion is disabled (<see cref="ParserConfiguration.ResponseFileTokenReplacer"/>
/// set to <see langword="null"/>) so that argument values beginning with <c>@</c> are passed through as
/// literal text rather than being interpreted as <c>@responsefile</c> indirection. winapp is an
/// app-automation CLI that is frequently used to drive chat/assistant UIs where <c>@mention</c> values
/// are common (e.g. <c>winapp ui search "@assistant"</c>); it does not need response-file support, and
/// leaving it enabled silently swallowed such values at parse time (issue #619).
/// </para>
/// </remarks>
internal static class WinAppParserConfiguration
{
    public static ParserConfiguration Default { get; } = new()
    {
        EnablePosixBundling = false,
        ResponseFileTokenReplacer = null
    };
}

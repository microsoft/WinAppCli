// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

/// <summary>
/// Provides a short description for a command, used in the help listing.
/// The long description (set via the Command constructor) remains used for
/// --cli-schema, LLM documentation, and individual command --help pages.
/// </summary>
internal interface IShortDescription
{
    string ShortDescription { get; }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Result of ensuring a boolean MSBuild property is set to <c>true</c> in a .csproj.
/// </summary>
internal enum CsprojPropertyOutcome
{
    /// <summary>The property was missing and was added.</summary>
    Added,

    /// <summary>The property existed with a non-true value and was updated to <c>true</c>.</summary>
    Updated,

    /// <summary>The property already existed and was set to <c>true</c>; the file was left unchanged.</summary>
    AlreadyTrue,

    /// <summary>
    /// The file could not be updated (it does not exist, or no insertion point such as a
    /// &lt;PropertyGroup&gt; was found). The file was left unchanged.
    /// </summary>
    NotModified,
}

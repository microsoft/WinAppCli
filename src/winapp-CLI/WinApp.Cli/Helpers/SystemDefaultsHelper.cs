// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Helpers;

internal static partial class SystemDefaultsHelper
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static string GetDefaultPackageName(DirectoryInfo dir)
    {
        var folder = dir.Name;
        var normalized = WhitespaceRegex().Replace(folder.Trim(), "-").ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "app" : normalized;
    }

    public static string GetDefaultPublisherCN()
    {
        return BuildPublisherCN(Environment.UserName);
    }

    /// <summary>
    /// Builds a <c>CN=</c> publisher value from an OS user name, falling back to
    /// <c>"Developer"</c> when the name is missing or whitespace. Extracted as a pure
    /// function so the whitespace-fallback branch can be unit tested deterministically.
    /// </summary>
    internal static string BuildPublisherCN(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            user = "Developer";
        }

        return $"CN={user}";
    }

    public static string GetDefaultDescription()
    {
        return "My Application";
    }
}

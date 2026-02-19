// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Provides utilities for resolving $placeholder$ tokens in manifest content.
/// Supports Visual Studio-style tokens like $targetnametoken$ and $targetentrypoint$.
/// </summary>
internal static partial class PlaceholderHelper
{
    /// <summary>
    /// Well-known placeholder for the executable name (without extension).
    /// Used in Executable attribute, e.g. Executable="$targetnametoken$.exe"
    /// </summary>
    public const string TargetNameToken = "targetnametoken";

    /// <summary>
    /// Well-known placeholder for the application entry point.
    /// Replaced with "Windows.FullTrustApplication" for desktop apps.
    /// </summary>
    public const string TargetEntryPointToken = "targetentrypoint";

    /// <summary>
    /// The default entry point value used to replace $targetentrypoint$.
    /// </summary>
    public const string FullTrustEntryPoint = "Windows.FullTrustApplication";

    /// <summary>
    /// Regex matching $placeholder$ tokens (dollar-sign delimited identifiers).
    /// </summary>
    [GeneratedRegex(@"\$([a-zA-Z_][a-zA-Z0-9_]*)\$", RegexOptions.None, "en-US")]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Built-in replacements that are always applied (e.g. $targetentrypoint$).
    /// </summary>
    private static readonly Dictionary<string, string> BuiltInReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        [TargetEntryPointToken] = FullTrustEntryPoint
    };

    /// <summary>
    /// Replaces all $key$ placeholders in the content with the corresponding values
    /// from the replacements dictionary. Matching is case-insensitive on the key.
    /// </summary>
    /// <param name="content">The manifest content containing placeholders.</param>
    /// <param name="replacements">Dictionary mapping placeholder names to replacement values.</param>
    /// <returns>The content with all matching placeholders resolved.</returns>
    public static string ReplacePlaceholders(string content, IDictionary<string, string> replacements)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        if (replacements == null || replacements.Count == 0)
        {
            return ReplacePlaceholders(content);
        }

        return PlaceholderPattern().Replace(content, match =>
        {
            var key = match.Groups[1].Value;
            foreach (var replacement in replacements)
            {
                if (string.Equals(replacement.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return replacement.Value;
                }
            }
            // Check built-in replacements
            if (BuiltInReplacements.TryGetValue(key, out var builtIn))
            {
                return builtIn;
            }
            // If no replacement found, leave the placeholder as-is
            return match.Value;
        });
    }

    /// <summary>
    /// Replaces only built-in placeholders (e.g. $targetentrypoint$) in the content.
    /// </summary>
    public static string ReplacePlaceholders(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        return PlaceholderPattern().Replace(content, match =>
        {
            var key = match.Groups[1].Value;
            if (BuiltInReplacements.TryGetValue(key, out var builtIn))
            {
                return builtIn;
            }
            return match.Value;
        });
    }

    /// <summary>
    /// Checks the content for any remaining $placeholder$ tokens and throws
    /// an <see cref="InvalidOperationException"/> listing them if found.
    /// </summary>
    /// <param name="content">The manifest content to validate.</param>
    /// <exception cref="InvalidOperationException">Thrown when unresolved placeholders remain.</exception>
    public static void ThrowIfUnresolvedPlaceholders(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        var matches = PlaceholderPattern().Matches(content);
        if (matches.Count > 0)
        {
            var uniqueTokens = matches
                .Select(m => $"${m.Groups[1].Value}$")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            throw new InvalidOperationException(
                $"The manifest contains unresolved placeholders: {string.Join(", ", uniqueTokens)}. " +
                "Edit the manifest to replace them or use --executable to specify the relative path to the exe.");
        }
    }

    /// <summary>
    /// Returns true if the given value contains any $placeholder$ token.
    /// </summary>
    public static bool ContainsPlaceholders(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return PlaceholderPattern().IsMatch(value);
    }
}

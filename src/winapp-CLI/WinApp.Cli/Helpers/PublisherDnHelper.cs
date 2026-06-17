// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure helper for publisher distinguished name (DN) operations:
/// validation, normalization, display-name extraction, and XML-safe formatting.
/// </summary>
internal static class PublisherDnHelper
{
    /// <summary>
    /// Returns true if the input is already a valid X.500 distinguished name
    /// (e.g., "CN=Name", "OU=Finance, DC=corp, DC=com").
    /// </summary>
    public static bool IsDistinguishedName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        try
        {
            _ = new X500DistinguishedName(input);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes publisher input to a valid X.500 distinguished name.
    /// If the input is already a valid DN, it is returned as-is.
    /// Bare names (without an attribute type prefix) are wrapped with CN=.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when input is empty or whitespace.</exception>
    public static string Normalize(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            throw new ArgumentException("Publisher name cannot be empty.", nameof(publisher));
        }

        // Strip wrapper quotes only if the entire value is enclosed in matching quotes.
        // Do NOT strip individual quote characters — they may be part of a valid DN value
        // (e.g., CN="Company, Inc." has intentional internal quotes).
        var trimmed = publisher.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Publisher name cannot be empty.", nameof(publisher));
        }

        if (IsDistinguishedName(trimmed))
        {
            return trimmed;
        }

        return $"CN={trimmed}";
    }

    /// <summary>
    /// Extracts a human-friendly display name from a distinguished name.
    /// For simple CN-only DNs, returns the CN value (e.g., "CN=Foo" → "Foo").
    /// For multi-component or non-CN DNs, returns the full DN unchanged.
    /// </summary>
    public static string GetDisplayName(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return distinguishedName;
        }

        // If it contains a comma, it's multi-component — return full DN
        // But first check if the comma is inside quotes (escaped)
        if (HasMultipleComponents(distinguishedName))
        {
            return distinguishedName;
        }

        // Single-component: strip the attribute type prefix if it's CN=
        if (distinguishedName.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            var value = distinguishedName[3..];
            // Strip surrounding quotes from the value if present
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            return value;
        }

        // Non-CN single-component (e.g., "OU=Finance") — return full DN
        return distinguishedName;
    }

    /// <summary>
    /// Returns true if the DN has multiple RDN components (unquoted commas).
    /// </summary>
    private static bool HasMultipleComponents(string dn)
    {
        bool inQuotes = false;
        for (int i = 0; i < dn.Length; i++)
        {
            char c = dn[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == '\\' && i + 1 < dn.Length)
            {
                i++; // skip escaped character
            }
            else if (c == ',' && !inQuotes)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// XML-escapes a DN value so it is safe to embed inside an XML attribute.
    /// Handles quotes, ampersands, angle brackets, etc.
    /// </summary>
    public static string XmlEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}

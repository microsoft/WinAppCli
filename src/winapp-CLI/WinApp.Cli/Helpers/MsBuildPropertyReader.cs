// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Parses the stdout of <c>dotnet build/msbuild --getProperty:...</c> into a property dictionary.
/// </summary>
/// <remarks>
/// The dotnet SDK returns two different shapes depending on how many properties were requested:
/// <list type="bullet">
/// <item><description>A <b>single</b> <c>--getProperty</c> returns a raw scalar (e.g. <c>WinExe</c>).</description></item>
/// <item><description><b>Multiple</b> <c>--getProperty</c> return JSON: <c>{ "Properties": { "Name": "Value", ... } }</c>.</description></item>
/// </list>
/// This helper accepts either shape. Pure and side-effect free so it can be unit tested without a build.
/// </remarks>
internal static class MsBuildPropertyReader
{
    /// <summary>
    /// Parses <paramref name="stdout"/> for the requested properties.
    /// </summary>
    /// <param name="stdout">Raw stdout captured from the dotnet invocation.</param>
    /// <param name="requestedNames">
    /// The property names that were requested. When exactly one name is requested and the output is
    /// not JSON, the whole (trimmed) output is treated as that property's value.
    /// </param>
    /// <returns>
    /// A case-insensitive map of property name to value. Values may be empty strings (a property that
    /// evaluated to empty). Missing properties are simply absent from the map.
    /// </returns>
    public static IReadOnlyDictionary<string, string> Parse(string stdout, IReadOnlyList<string> requestedNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stdout is null)
        {
            return result;
        }

        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return result;
        }

        // JSON shape: { "Properties": { "Name": "Value", ... } }. Normally stdout is clean JSON, but be
        // tolerant of a diagnostic preamble before the object AND of trailing content after it. We only
        // accept an object that actually carries a "Properties" object, so a scalar value that merely
        // contains a '{' can never be misread as the JSON shape.
        if (TryReadPropertiesObject(trimmed, result))
        {
            return result;
        }

        // Scalar shape: a single requested property whose raw value is the whole output.
        if (requestedNames.Count == 1)
        {
            result[requestedNames[0]] = trimmed;
        }

        return result;
    }

    /// <summary>
    /// Parses the stdout of <c>dotnet build/msbuild --getItem:...</c> into a map of item name to the
    /// list of item identities (the <c>Include</c> values). The SDK emits
    /// <c>{ "Items": { "ItemName": [ { "Identity": "…", … }, … ], … } }</c> (combined with
    /// <c>{ "Properties": … }</c> when properties are requested too). Tolerant of a diagnostic preamble
    /// before the object and trailing content after it, mirroring <see cref="Parse"/>. Pure and
    /// side-effect free so it can be unit tested without a build. Missing item groups are simply absent.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseItems(string stdout)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return result;
        }

        TryReadItemsObject(stdout.Trim(), result);
        return result;
    }

    /// <summary>
    /// Scans <paramref name="text"/> for the first JSON object that carries an <c>"Items"</c> object and,
    /// if found, fills <paramref name="result"/> with each item group's identities. Uses the same
    /// candidate-brace scan as <see cref="TryReadPropertiesObject"/> so a diagnostic preamble/trailer
    /// cannot defeat parsing.
    /// </summary>
    private static void TryReadItemsObject(string text, Dictionary<string, IReadOnlyList<string>> result)
    {
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var braceIndex = text.IndexOf('{', searchStart);
            if (braceIndex < 0)
            {
                return;
            }

            var candidate = text[braceIndex..];
            var bytes = System.Text.Encoding.UTF8.GetBytes(candidate);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            try
            {
                if (JsonDocument.TryParseValue(ref reader, out var doc))
                {
                    using (doc)
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("Items", out var items) &&
                            items.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var group in items.EnumerateObject().Where(g => g.Value.ValueKind == JsonValueKind.Array))
                            {
                                var identities = new List<string>();
                                foreach (var item in group.Value.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.Object &&
                                        item.TryGetProperty("Identity", out var id) &&
                                        id.ValueKind == JsonValueKind.String)
                                    {
                                        var value = id.GetString();
                                        if (!string.IsNullOrEmpty(value))
                                        {
                                            identities.Add(value);
                                        }
                                    }
                                }

                                result[group.Name] = identities;
                            }

                            return;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // This '{' did not begin a valid JSON value — try the next one (preamble brace).
            }

            searchStart = braceIndex + 1;
        }
    }

    /// <summary>
    /// Scans <paramref name="text"/> for the first JSON object that is a <c>{ "Properties": {...} }</c>
    /// envelope and, if found, fills <paramref name="result"/> and returns <c>true</c>. Each <c>'{'</c>
    /// is tried as a candidate start (skipping a non-JSON preamble brace), and a single JSON value is
    /// read via <see cref="JsonDocument.TryParseValue"/> so trailing diagnostics after the object are
    /// ignored rather than causing a parse failure.
    /// </summary>
    private static bool TryReadPropertiesObject(string text, Dictionary<string, string> result)
    {
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var braceIndex = text.IndexOf('{', searchStart);
            if (braceIndex < 0)
            {
                return false;
            }

            var candidate = text[braceIndex..];
            var bytes = System.Text.Encoding.UTF8.GetBytes(candidate);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            try
            {
                if (JsonDocument.TryParseValue(ref reader, out var doc))
                {
                    using (doc)
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("Properties", out var props) &&
                            props.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in props.EnumerateObject())
                            {
                                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                    ? prop.Value.GetString() ?? string.Empty
                                    : prop.Value.ToString();
                            }

                            return true;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // This '{' did not begin a valid JSON value — try the next one (preamble brace).
            }

            searchStart = braceIndex + 1;
        }

        return false;
    }
}

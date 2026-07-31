// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Parses .NET XML documentation files (the same descriptions Visual Studio
/// IntelliSense shows) and merges their summaries onto parsed metadata types.
/// </summary>
internal static partial class XmlDocParser
{
    /// <summary>
    /// Parses a .NET XML documentation file and returns a dictionary mapping
    /// member doc IDs (e.g., "T:Namespace.Type", "P:Namespace.Type.Property")
    /// to their summary text.
    /// </summary>
    public static Dictionary<string, string> ParseFile(string xmlPath)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Ignore
            });

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "member")
                {
                    string? name = reader.GetAttribute("name");
                    if (name == null)
                    {
                        continue;
                    }

                    string innerXml = reader.ReadInnerXml();
                    string? summary = ExtractSummary(innerXml);
                    if (summary != null)
                    {
                        docs[name] = summary;
                    }
                }
            }
        }
        catch
        {
            // Malformed or unreadable XML docs are non-fatal: descriptions are an
            // enrichment on top of the metadata, so skip this file silently rather
            // than writing to stderr (which would corrupt --json output).
        }
        return docs;
    }

    private static string? ExtractSummary(string innerXml)
    {
        int start = innerXml.IndexOf("<summary>", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += "<summary>".Length;

        int end = innerXml.IndexOf("</summary>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        string raw = innerXml.Substring(start, end - start);
        return CleanXmlText(raw);
    }

    private static string? CleanXmlText(string raw)
    {
        // Replace <see cref="..."/> with the short (final-segment) name.
        string text = SeeCrefRegex().Replace(raw, static m =>
        {
            string cref = CrefValueRegex().Match(m.Value).Groups[1].Value;
            int dot = cref.LastIndexOf('.');
            return dot >= 0 ? cref.Substring(dot + 1) : cref;
        });

        // Strip any remaining XML tags and collapse whitespace.
        text = AnyTagRegex().Replace(text, "");
        text = WhitespaceRegex().Replace(text, " ").Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Merges XML doc descriptions into parsed type/member data.
    /// </summary>
    public static void MergeDescriptions(List<WinMdTypeInfo> types, Dictionary<string, string> docs)
    {
        foreach (var type in types)
        {
            string typeKey = "T:" + type.FullName;
            if (docs.TryGetValue(typeKey, out string? typeDesc))
            {
                type.Description = typeDesc;
            }

            foreach (var member in type.Members)
            {
                string? memberKey = GetMemberDocKey(type.FullName, member);
                if (memberKey != null && docs.TryGetValue(memberKey, out string? memberDesc))
                {
                    member.Description = memberDesc;
                }
            }
        }
    }

    private static string? GetMemberDocKey(string typeFullName, WinMdMemberInfo member)
    {
        return member.Kind switch
        {
            MemberKind.Property => "P:" + typeFullName + "." + member.Name,
            MemberKind.Event => "E:" + typeFullName + "." + member.Name,
            MemberKind.Field => "F:" + typeFullName + "." + member.Name,
            MemberKind.Method => BuildMethodDocKey(typeFullName, member),
            _ => null
        };
    }

    private static string BuildMethodDocKey(string typeFullName, WinMdMemberInfo member)
    {
        string key = "M:" + typeFullName + "." + member.Name;
        if (member.Parameters != null && member.Parameters.Count > 0)
        {
            key += "(" + string.Join(",", member.Parameters.ConvertAll(p => p.Type)) + ")";
        }
        return key;
    }

    [GeneratedRegex("<see\\s+cref=\"[^\"]*\"\\s*/>")]
    private static partial Regex SeeCrefRegex();

    [GeneratedRegex("cref=\"([^\"]*)\"")]
    private static partial Regex CrefValueRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure static helper for extracting and validating file path references from manifest content.
/// </summary>
internal static partial class ManifestFileReferenceHelper
{
    private static readonly HashSet<string> KnownFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Executables and libraries
        ".exe", ".dll", ".winmd", ".sys", ".ocx",
        // Images
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".tiff", ".webp",
        // Configuration and data
        ".xml", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".config",
        // Web
        ".html", ".htm", ".css", ".js", ".ts", ".mjs", ".cjs", ".wasm",
        // Documents
        ".txt", ".md", ".pdf", ".rtf", ".csv",
        // Resources and assets
        ".resw", ".resx", ".pri", ".rc", ".resources", ".xaml", ".xsd", ".xsl", ".xslt",
        // Source code
        ".cs", ".cpp", ".c", ".h", ".hpp", ".idl", ".def", ".vcxproj", ".csproj", ".sln",
        // Fonts
        ".ttf", ".otf", ".woff", ".woff2",
        // Audio and video
        ".mp3", ".wav", ".ogg", ".mp4", ".avi", ".wmv",
        // Archives and packages
        ".zip", ".msix", ".appx", ".appxbundle", ".msixbundle", ".cab", ".msi", ".nupkg",
        // Certificates and signing
        ".pfx", ".cer", ".p7x", ".cat",
        // Misc
        ".man", ".manifest", ".rdp", ".lnk", ".url", ".appxmanifest",
    };

    /// <summary>
    /// Extracts all file path references from an AppxManifest XML document.
    /// Walks all attribute values and element text content looking for relative
    /// file paths (values with a file extension that are not URIs, versions, or
    /// other non-path values). Returns a deduplicated set of relative paths.
    /// </summary>
    internal static HashSet<string> ExtractAllFileReferencesFromManifest(string manifestContent)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var doc = XDocument.Parse(manifestContent);
            if (doc.Root == null)
            {
                return references;
            }

            foreach (var element in doc.Root.DescendantsAndSelf())
            {
                // Check attribute values
                foreach (var attr in element.Attributes())
                {
                    if (IsLikelyFilePath(attr.Value))
                    {
                        references.Add(NormalizePathSeparators(attr.Value.Trim()));
                    }
                }

                // Check text content (only leaf elements with no child elements)
                if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
                {
                    if (IsLikelyFilePath(element.Value))
                    {
                        references.Add(NormalizePathSeparators(element.Value.Trim()));
                    }
                }
            }
        }
        catch
        {
            // If manifest cannot be parsed, return empty set
        }

        return references;
    }

    /// <summary>
    /// Determines whether a string value looks like a relative file path.
    /// Rejects URIs, version strings, GUIDs, class names, and other non-path values.
    /// </summary>
    internal static bool IsLikelyFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();

        // Must contain a dot (for file extension)
        if (!value.Contains('.'))
        {
            return false;
        }

        // Reject URIs and namespaces
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject absolute paths and UNC paths
        if (Path.IsPathRooted(value) || value.StartsWith(@"\\"))
        {
            return false;
        }

        // Reject path traversal
        var pathSegments = value.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None);
        if (pathSegments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            return false;
        }

        // Reject values with invalid path characters
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        // Reject version-like strings (e.g., "1.0.0.0", "10.0.18362.0")
        if (VersionLikeRegex().IsMatch(value))
        {
            return false;
        }

        // Reject GUID-like strings
        if (Guid.TryParse(value, out _))
        {
            return false;
        }

        // Reject dotted identifiers that look like class/namespace names (e.g., "MyApp.App", "Windows.Universal")
        // Only accept values whose extension is in the known file extensions allow list
        var extension = Path.GetExtension(value);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        if (!KnownFileExtensions.Contains(extension))
        {
            return false;
        }

        return true;
    }

    internal static string NormalizePathSeparators(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$")]
    private static partial Regex VersionLikeRegex();
}

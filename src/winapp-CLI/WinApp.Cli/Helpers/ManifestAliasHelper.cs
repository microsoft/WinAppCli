// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Injects an AppExecutionAlias into an AppX manifest to enable
/// launching with package identity and stdio redirection.
/// </summary>
internal static partial class ManifestAliasHelper
{
    [GeneratedRegex(@"(<Package[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex PackageOpenTagRegex();

    [GeneratedRegex(@"(\s*</Application>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ApplicationCloseTagRegex();

    /// <summary>
    /// Generates a deterministic execution alias name from a package name.
    /// </summary>
    public static string GenerateAliasName(string packageName)
    {
        return $"winapp-run-{packageName}.exe";
    }

    /// <summary>
    /// Injects an AppExecutionAlias extension into the manifest content.
    /// Returns the modified manifest content.
    /// </summary>
    public static string InjectExecutionAlias(string manifestContent, string aliasName, string executableName)
    {
        // Add uap5 namespace if not present
        if (!manifestContent.Contains("xmlns:uap5", StringComparison.OrdinalIgnoreCase))
        {
            manifestContent = PackageOpenTagRegex().Replace(manifestContent,
                @"$1 xmlns:uap5=""http://schemas.microsoft.com/appx/manifest/uap/windows10/5""$2", 1);
        }

        // Add uap5 to IgnorableNamespaces if present
        manifestContent = AddToIgnorableNamespaces(manifestContent, "uap5");

        // Inject the AppExecutionAlias extension before </Application>
        var extensionXml = $@"
        <Extensions>
          <uap5:Extension Category=""windows.appExecutionAlias"" Executable=""{executableName}"" EntryPoint=""Windows.FullTrustApplication"">
            <uap5:AppExecutionAlias>
              <uap5:ExecutionAlias Alias=""{aliasName}"" />
            </uap5:AppExecutionAlias>
          </uap5:Extension>
        </Extensions>";

        manifestContent = ApplicationCloseTagRegex().Replace(manifestContent,
            $"{extensionXml}$1", 1);

        return manifestContent;
    }

    private static string AddToIgnorableNamespaces(string content, string ns)
    {
        var match = IgnorableNamespacesRegex().Match(content);
        if (match.Success)
        {
            var existing = match.Groups[1].Value;
            if (!existing.Contains(ns, StringComparison.OrdinalIgnoreCase))
            {
                content = content.Replace(match.Value, $@"IgnorableNamespaces=""{existing} {ns}""");
            }
        }
        return content;
    }

    [GeneratedRegex(@"IgnorableNamespaces\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex IgnorableNamespacesRegex();
}

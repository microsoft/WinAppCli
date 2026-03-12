// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Static helper for MRT (Modern Resource Technology) asset qualification,
/// expansion, and file copying.
/// </summary>
internal static partial class MrtAssetHelper
{
    // Language (en, en-US, pt-BR, zh-Hans, etc.) – bare token
    [GeneratedRegex(@"^[a-zA-Z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageQualifierRegex();

    [GeneratedRegex(@"^scale-(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    // scale-100, scale-200, etc.
    private static partial Regex ScaleQualifierRegex();

    // theme-dark, theme-light
    [GeneratedRegex(@"^theme-(light|dark)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ThemeQualifierRegex();

    // contrast-standard, contrast-high
    [GeneratedRegex(@"^contrast-(standard|high)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ContrastQualifierRegex();

    // dxfeaturelevel-9 / 10 / 11
    [GeneratedRegex(@"^dxfeaturelevel-(9|10|11)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DxFeatureLevelQualifierRegex();

    // device-family-desktop / xbox / team / iot / mobile
    [GeneratedRegex(@"^device-family-(desktop|mobile|team|xbox|iot)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceFamilyQualifierRegex();

    // homeregion-US, homeregion-JP, ...
    [GeneratedRegex(@"^homeregion-[A-Za-z]{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex HomeRegionQualifierRegex();

    // configuration-debug, configuration-retail, etc.
    [GeneratedRegex(@"^configuration-[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationQualifierRegex();

    // targetsize-16, targetsize-24, targetsize-256, ...
    [GeneratedRegex(@"^targetsize-(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TargetSizeQualifierRegex();

    // altform-unplated, altform-lightunplated, etc.
    [GeneratedRegex(@"^altform-[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AltFormQualifierRegex();

    internal static readonly HashSet<string> PriIncludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".ico",
        ".svg"
    };

    internal static List<(FileInfo SourceFile, string RelativePath)> ExpandManifestReferencedFiles(
        DirectoryInfo manifestDir,
        IEnumerable<string> referencedFiles,
        TaskContext? taskContext,
        Func<FileInfo, bool>? includeFile = null)
    {
        var expandedFilesByRelativePath = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativeFilePath in referencedFiles)
        {
            var logicalSourceFile = new FileInfo(Path.Combine(manifestDir.FullName, relativeFilePath));
            var sourceDir = logicalSourceFile.Directory;

            if (sourceDir is null || !sourceDir.Exists)
            {
                taskContext?.AddDebugMessage($"{UiSymbols.Warning} Source directory not found for referenced file: {relativeFilePath}");
                continue;
            }

            var logicalBaseName = Path.GetFileNameWithoutExtension(logicalSourceFile.Name);
            var variantBaseName = GetMrtVariantBaseName(logicalBaseName);
            var extension = logicalSourceFile.Extension;

            var searchPattern = variantBaseName + "*" + extension;
            var candidates = sourceDir.EnumerateFiles(searchPattern);
            var anyIncludedForLogical = false;

            foreach (var candidateFile in candidates)
            {
                if (includeFile != null && !includeFile(candidateFile))
                {
                    continue;
                }

                var candidateNameWithoutExtension = Path.GetFileNameWithoutExtension(candidateFile.Name);
                if (!IsMrtVariantName(variantBaseName, candidateNameWithoutExtension))
                {
                    continue;
                }

                var relativeDir = Path.GetDirectoryName(relativeFilePath);
                var candidateRelativePath = string.IsNullOrEmpty(relativeDir)
                    ? candidateFile.Name
                    : Path.Combine(relativeDir, candidateFile.Name);

                expandedFilesByRelativePath[candidateRelativePath] = candidateFile;
                anyIncludedForLogical = true;
            }

            if (!anyIncludedForLogical && logicalSourceFile.Exists && (includeFile == null || includeFile(logicalSourceFile)))
            {
                expandedFilesByRelativePath[relativeFilePath] = logicalSourceFile;
            }
            else if (!anyIncludedForLogical && !logicalSourceFile.Exists)
            {
                taskContext?.AddDebugMessage($"{UiSymbols.Warning} Referenced file not found (no MRT variants): {logicalSourceFile}");
            }
        }

        return [.. expandedFilesByRelativePath
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Value, pair.Key))];
    }

    internal static List<(FileInfo SourceFile, string RelativePath)> GetExpandedManifestReferencedFiles(
        FileInfo manifestPath,
        TaskContext taskContext)
    {
        var manifestDir = manifestPath.Directory;
        if (manifestDir == null)
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Manifest directory not found for: {manifestPath}");
            return [];
        }

        taskContext.AddDebugMessage($"{UiSymbols.Note} Reading manifest: {manifestPath}");

        var assetReferences = ManifestService.ExtractAssetReferencesFromManifest(manifestPath, taskContext);
        var referencedFiles = assetReferences.Select(a => a.RelativePath);
        return ExpandManifestReferencedFiles(manifestDir, referencedFiles, taskContext);
    }

    internal static void CopyAllAssets(List<(FileInfo SourceFile, string RelativePath)> expandedFiles, DirectoryInfo targetDir, TaskContext taskContext)
    {
        var (copied, skipped) = IncrementalCopyHelper.CopyFiles(expandedFiles, targetDir);
        taskContext.AddDebugMessage($"{UiSymbols.Note} Manifest resources: {copied} copied, {skipped} unchanged");
    }

    // ltr / rtl
    private static bool IsLayoutDirectionQualifier(string token)
    {
        return token.Equals("ltr", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("rtl", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSingleQualifierToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        return LanguageQualifierRegex().IsMatch(token)
            || ScaleQualifierRegex().IsMatch(token)
            || ThemeQualifierRegex().IsMatch(token)
            || ContrastQualifierRegex().IsMatch(token)
            || DxFeatureLevelQualifierRegex().IsMatch(token)
            || DeviceFamilyQualifierRegex().IsMatch(token)
            || HomeRegionQualifierRegex().IsMatch(token)
            || ConfigurationQualifierRegex().IsMatch(token)
            || TargetSizeQualifierRegex().IsMatch(token)
            || AltFormQualifierRegex().IsMatch(token)
            || IsLayoutDirectionQualifier(token);
    }

    internal static bool IsQualifierToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var parts = token.Split('_');

        foreach (var part in parts)
        {
            if (!IsSingleQualifierToken(part))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="candidateNameWithoutExtension"/> is a valid MRT
    /// variant of the logical base name (dots allowed in base name).
    /// </summary>
    internal static bool IsMrtVariantName(string logicalBaseName, string candidateNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(logicalBaseName) || string.IsNullOrWhiteSpace(candidateNameWithoutExtension))
        {
            return false;
        }

        // Split by '.'; "Logo.scale-200.theme-dark" -> ["Logo", "scale-200", "theme-dark"]
        var parts = candidateNameWithoutExtension.Split('.');

        if (parts.Length == 0)
        {
            return false;
        }

        // First token must match logical base name (case-insensitive)
        if (!parts[0].Equals(logicalBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // No qualifiers -> exact logical name, valid
        if (parts.Length == 1)
        {
            return true;
        }

        // All remaining tokens must be valid MRT qualifiers
        for (int i = 1; i < parts.Length; i++)
        {
            if (!IsQualifierToken(parts[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// For a qualified logical name like "Logo.scale-100" or "Logo.targetsize-24_altform-unplated",
    /// returns the unqualified asset family base (e.g. "Logo").
    /// If the name has no trailing qualifier tokens, returns the original name unchanged.
    /// </summary>
    internal static string GetMrtVariantBaseName(string logicalBaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalBaseName);

        var parts = logicalBaseName.Split('.');
        if (parts.Length <= 1)
        {
            return logicalBaseName;
        }

        // Find the earliest segment where every remaining segment is a valid qualifier token.
        for (int i = 1; i < parts.Length; i++)
        {
            var allRemainingAreQualifiers = true;
            for (int j = i; j < parts.Length; j++)
            {
                if (!IsQualifierToken(parts[j]))
                {
                    allRemainingAreQualifiers = false;
                    break;
                }
            }

            if (allRemainingAreQualifiers)
            {
                return string.Join('.', parts[..i]);
            }
        }

        return logicalBaseName;
    }
}

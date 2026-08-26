// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Resolves an architecture-specific publish profile when required by the effective build, without forcing a
/// global MSBuild Platform onto the project-reference graph.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// Resolves a required architecture-specific profile while preserving the established RID-only behavior
    /// for projects that already build successfully. A profile is required when the effective configuration
    /// enables trimming without a self-contained deployment; the .NET SDK rejects that combination. NativeAOT
    /// and already-self-contained projects retain RID-only behavior because their normal build does not need
    /// the profile. The profile sets Platform locally in the app, so AnyCPU references keep their own Platform.
    /// </summary>
    private async Task<ProjectRunOptions> ResolveRequiredPublishProfileAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDirectory,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        var candidate = ResolvePublishProfileFallback(csproj, options);
        if (string.IsNullOrWhiteSpace(candidate.PublishProfile)
            || UserSpecifiesProperty(options.Properties, "SelfContained"))
        {
            return options;
        }

        var arguments = BuildEvaluateArguments(csproj, options, csWinRTMetadataFolder);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(arguments));

        int exitCode;
        string output;
        try
        {
            (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(
                workingDirectory,
                arguments,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception)
        {
            logger.LogDebug(
                "{UISymbol} Could not start publish-profile requirement evaluation; keeping RID-only inputs.",
                UiSymbols.Note);
            return options;
        }
        catch (InvalidOperationException)
        {
            logger.LogDebug(
                "{UISymbol} Could not evaluate whether an architecture-specific publish profile is required; keeping RID-only inputs.",
                UiSymbols.Note);
            return options;
        }

        if (exitCode != 0)
        {
            logger.LogDebug(
                "{UISymbol} Publish-profile requirement evaluation exited {ExitCode}; keeping RID-only inputs.",
                UiSymbols.Note,
                exitCode);
            return options;
        }

        var properties = MsBuildPropertyReader.Parse(output, RequestedProperties);
        var publishTrimmed = IsTrue(GetProp(properties, "PublishTrimmed"));
        var publishAot = IsTrue(GetProp(properties, "PublishAot"));
        var selfContained = IsTrue(GetProp(properties, "SelfContained"));

        return publishTrimmed && !publishAot && !selfContained
            ? candidate
            : options;
    }

    internal static ProjectRunOptions ResolvePublishProfileFallback(FileInfo csproj, ProjectRunOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublishProfile)
            || !string.IsNullOrWhiteSpace(options.Platform)
            || UserSpecifiesProperty(options.Properties, "Platform")
            || UserSpecifiesProperty(options.Properties, "PublishProfile")
            || UserSpecifiesProperty(options.Properties, "PublishProfileFullPath"))
        {
            return options;
        }

        var declarations = ReadPlatformDependentPublishProfiles(csproj);
        if (declarations.Count == 0)
        {
            return options;
        }

        var platformToken = FindArchPlatformToken(csproj, options.Architecture) ?? options.Architecture;
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in declarations)
        {
            var expanded = ExpandPublishProfile(declaration, platformToken, options.Configuration);
            if (expanded is null
                || !TryResolvePublishProfileFile(csproj, expanded, out var profile)
                || !PublishProfileTargetsArchitecture(profile, options.Architecture)
                || !PublishProfileEnablesSelfContained(profile)
                || ProjectReferenceClosureContainsPublishProfile(csproj, expanded))
            {
                continue;
            }

            matches.Add(expanded);
        }

        return matches.Count == 1
            ? options with { PublishProfile = matches.Single() }
            : options;
    }

    private static List<string> ReadPlatformDependentPublishProfiles(FileInfo project)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(project.FullName);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (XmlException)
        {
            return [];
        }

        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "PublishProfile")
            .Select(element => element.Value.Trim())
            .Where(value => value.Contains("$(Platform", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExpandPublishProfile(string value, string platform, string configuration)
    {
        var expanded = ReplacePropertyTransforms(value, "Platform", platform);
        expanded = ReplacePropertyTransforms(expanded, "Configuration", configuration);

        return expanded.Contains("$(", StringComparison.Ordinal)
            ? null
            : expanded.Trim();
    }

    private static string ReplacePropertyTransforms(string value, string property, string replacement)
    {
        var expanded = value
            .Replace($"$({property}.ToLowerInvariant())", replacement.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace($"$({property}.ToLower())", replacement.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace($"$({property}.ToUpperInvariant())", replacement.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace($"$({property}.ToUpper())", replacement.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase);

        return expanded.Replace($"$({property})", replacement, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolvePublishProfileFile(FileInfo project, string publishProfile, out FileInfo profile)
    {
        profile = null!;
        var projectDirectory = project.Directory;
        if (projectDirectory is null || string.IsNullOrWhiteSpace(publishProfile))
        {
            return false;
        }

        try
        {
            // Microsoft.NET.Sdk.ImportPublishProfile.targets discards directory components from
            // $(PublishProfile) and resolves the basename under Properties\PublishProfiles.
            var profileName = Path.GetFileNameWithoutExtension(publishProfile);
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return false;
            }

            var candidate = new FileInfo(Path.Join(
                projectDirectory.FullName,
                "Properties",
                "PublishProfiles",
                profileName + ".pubxml"));

            profile = candidate;
            return candidate.Exists;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool PublishProfileTargetsArchitecture(FileInfo profile, string architecture)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(profile.FullName);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (XmlException)
        {
            return false;
        }

        var declaredArchitectures = document
            .Descendants()
            .Select(element => element.Name.LocalName switch
            {
                "RuntimeIdentifier" => RunArchHelper.ArchitectureFromRid(element.Value),
                "Platform" => RunArchHelper.NormalizeArchitecture(element.Value),
                _ => null,
            })
            .OfType<string>()
            .ToList();

        return declaredArchitectures.Count > 0
            && declaredArchitectures.All(value =>
                value.Equals(architecture, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PublishProfileEnablesSelfContained(FileInfo profile)
    {
        try
        {
            var document = XDocument.Load(profile.FullName);
            return document
                .Descendants()
                .Where(element => element.Name.LocalName == "SelfContained")
                .Any(element => IsTrue(element.Value.Trim()));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool ProjectReferenceClosureContainsPublishProfile(
        FileInfo start,
        string publishProfile)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.FullName };
        var queue = new Queue<FileInfo>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var include in ReadProjectReferenceIncludes(current))
            {
                if (!TryResolveReferencePath(current, include, out var reference))
                {
                    return true;
                }

                if (TryResolvePublishProfileFile(reference, publishProfile, out _))
                {
                    return true;
                }

                if (visited.Add(reference.FullName))
                {
                    if (visited.Count > MaxProjectReferenceClosure)
                    {
                        return true;
                    }

                    queue.Enqueue(reference);
                }
            }
        }

        return false;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase);
}

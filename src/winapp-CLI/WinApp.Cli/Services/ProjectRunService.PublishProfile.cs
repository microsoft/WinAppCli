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
        if (!CanInferPublishProfile(options)
            || !DeclaresPlatformDependentPublishProfile(csproj))
        {
            return options;
        }

        var currentProperties = await TryEvaluatePublishProfilePropertiesAsync(
            csproj,
            options,
            workingDirectory,
            csWinRTMetadataFolder,
            cancellationToken);

        if (currentProperties is null
            || !RequiresSelfContainedProfile(currentProperties)
            || HasAuthoritativeProfileSelector(currentProperties))
        {
            return options;
        }

        var platform = FindArchPlatformToken(csproj, options.Architecture) ?? options.Architecture;
        var platformProperties = await TryEvaluatePublishProfilePropertiesAsync(
            csproj,
            options with { Platform = platform },
            workingDirectory,
            csWinRTMetadataFolder,
            cancellationToken);

        return platformProperties is null
            ? options
            : ResolvePublishProfileFallback(csproj, options, currentProperties, platformProperties);
    }

    private async Task<IReadOnlyDictionary<string, string>?> TryEvaluatePublishProfilePropertiesAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDirectory,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
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
            return null;
        }
        catch (InvalidOperationException)
        {
            logger.LogDebug(
                "{UISymbol} Could not evaluate whether an architecture-specific publish profile is required; keeping RID-only inputs.",
                UiSymbols.Note);
            return null;
        }

        if (exitCode != 0)
        {
            logger.LogDebug(
                "{UISymbol} Publish-profile requirement evaluation exited {ExitCode}; keeping RID-only inputs.",
                UiSymbols.Note,
                exitCode);
            return null;
        }

        return MsBuildPropertyReader.Parse(output, RequestedProperties);
    }

    internal static ProjectRunOptions ResolvePublishProfileFallback(
        FileInfo csproj,
        ProjectRunOptions options,
        IReadOnlyDictionary<string, string> currentProperties,
        IReadOnlyDictionary<string, string> platformProperties)
    {
        if (!CanInferPublishProfile(options)
            || !RequiresSelfContainedProfile(currentProperties)
            || HasAuthoritativeProfileSelector(currentProperties)
            || HasAuthoritativeProfileSelector(platformProperties))
        {
            return options;
        }

        var currentProfile = GetProp(currentProperties, "PublishProfile");
        var candidateProfile = GetProp(platformProperties, "PublishProfile");
        if (string.IsNullOrWhiteSpace(candidateProfile)
            || string.Equals(currentProfile, candidateProfile, StringComparison.OrdinalIgnoreCase)
            || !IsTrue(GetProp(platformProperties, "PublishProfileImported"))
            || !IsTrue(GetProp(platformProperties, "SelfContained"))
            || IsTrue(GetProp(platformProperties, "PublishAot"))
            || !TryGetImportedPublishProfileFile(csproj, platformProperties, out var profile)
            || !PublishProfileTargetsArchitecture(profile, options.Architecture))
        {
            return options;
        }

        var profileToken = Path.GetFileName(profile.FullName);
        if (ProjectReferenceClosureContainsPublishProfile(csproj, profileToken))
        {
            return options;
        }

        return options with { PublishProfile = profileToken };
    }

    private static bool CanInferPublishProfile(ProjectRunOptions options) =>
        string.IsNullOrWhiteSpace(options.PublishProfile)
        && string.IsNullOrWhiteSpace(options.Platform)
        && !UserSpecifiesProperty(options.Properties, "Platform")
        && !UserSpecifiesProperty(options.Properties, "SelfContained")
        && !UserSpecifiesProperty(options.Properties, "PublishProfile")
        && !UserSpecifiesProperty(options.Properties, "PublishProfileName")
        && !UserSpecifiesProperty(options.Properties, "PublishProfileFullPath")
        && !UserSpecifiesProperty(options.Properties, "WebPublishProfileFile");

    private static bool DeclaresPlatformDependentPublishProfile(FileInfo project)
    {
        try
        {
            return XDocument
                .Load(project.FullName)
                .Descendants()
                .Any(element =>
                    element.Name.LocalName == "PublishProfile"
                    && element.Value.Contains("$(Platform", StringComparison.OrdinalIgnoreCase));
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

    private static bool RequiresSelfContainedProfile(IReadOnlyDictionary<string, string> properties) =>
        IsTrue(GetProp(properties, "PublishTrimmed"))
        && !IsTrue(GetProp(properties, "PublishAot"))
        && !IsTrue(GetProp(properties, "SelfContained"));

    private static bool HasAuthoritativeProfileSelector(IReadOnlyDictionary<string, string> properties)
    {
        var publishProfile = GetProp(properties, "PublishProfile");
        var publishProfileName = GetProp(properties, "PublishProfileName");
        var publishProfileFullPath = GetProp(properties, "PublishProfileFullPath");
        var webPublishProfileFile = GetProp(properties, "WebPublishProfileFile");

        if (string.IsNullOrWhiteSpace(publishProfile))
        {
            return !string.IsNullOrWhiteSpace(publishProfileName)
                || !string.IsNullOrWhiteSpace(publishProfileFullPath)
                || !string.IsNullOrWhiteSpace(webPublishProfileFile);
        }

        try
        {
            var expectedName = Path.GetFileNameWithoutExtension(publishProfile);
            if (!string.Equals(publishProfileName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var profileRoot = GetProp(properties, "_PublishProfileRootFolder");
            if (string.IsNullOrWhiteSpace(profileRoot))
            {
                return !string.IsNullOrWhiteSpace(publishProfileFullPath)
                    || !string.IsNullOrWhiteSpace(webPublishProfileFile);
            }

            var expectedFullPath = Path.GetFullPath(Path.Join(profileRoot, expectedName + ".pubxml"));
            if (!PathsEqual(publishProfileFullPath, expectedFullPath))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(webPublishProfileFile)
                && !PathsEqual(webPublishProfileFile, publishProfileFullPath);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private static bool TryGetImportedPublishProfileFile(
        FileInfo project,
        IReadOnlyDictionary<string, string> properties,
        out FileInfo profile)
    {
        profile = null!;
        var projectDirectory = project.Directory;
        var publishProfileFullPath = GetProp(properties, "PublishProfileFullPath");
        var webPublishProfileFile = GetProp(properties, "WebPublishProfileFile");
        if (projectDirectory is null
            || string.IsNullOrWhiteSpace(publishProfileFullPath)
            || !PathsEqual(publishProfileFullPath, webPublishProfileFile))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(publishProfileFullPath);
            var projectRoot = Path.GetFullPath(projectDirectory.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            profile = new FileInfo(fullPath);
            return profile.Exists;
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

                var referenceDirectory = reference.Directory;
                if (referenceDirectory is null)
                {
                    return true;
                }

                var candidate = new FileInfo(Path.Join(
                    referenceDirectory.FullName,
                    "Properties",
                    "PublishProfiles",
                    publishProfile));
                if (candidate.Exists)
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

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
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
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;

namespace WinApp.Cli.Services;

internal class BundleValidationService : IBundleValidationService
{
    public IReadOnlyList<BundleValidationError> Validate(
        IReadOnlyList<AppxManifestDocument> sliceManifests,
        IReadOnlyList<string> detectedArchitectures,
        IReadOnlyList<DirectoryInfo> inputFolders)
    {
        var errors = new List<BundleValidationError>();

        // Architecture validation
        ValidateArchitectures(detectedArchitectures, inputFolders, errors);

        // Cross-slice manifest consistency
        if (sliceManifests.Count > 1)
        {
            ValidateIdentityConsistency(sliceManifests, inputFolders, errors);
            ValidateCapabilitiesConsistency(sliceManifests, inputFolders, errors);
            ValidateDependenciesConsistency(sliceManifests, inputFolders, errors);
            ValidateApplicationsConsistency(sliceManifests, inputFolders, errors);
        }

        return errors;
    }

    private static void ValidateArchitectures(
        IReadOnlyList<string> architectures,
        IReadOnlyList<DirectoryInfo> inputFolders,
        List<BundleValidationError> errors)
    {
        // Check for duplicates
        var archGroups = architectures
            .Select((arch, index) => (arch, folder: inputFolders[index]))
            .GroupBy(x => x.arch, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in archGroups)
        {
            var folders = group.Select(x => x.folder.FullName).ToList();
            errors.Add(new BundleValidationError(
                "Architecture",
                $"Duplicate architecture '{group.Key}' detected in multiple input folders.",
                folders));
        }

        // Check for all-neutral
        if (architectures.All(a => string.Equals(a, "neutral", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new BundleValidationError(
                "Architecture",
                "All input folders contain architecture-neutral binaries. A bundle requires at least one architecture-specific slice.",
                inputFolders.Select((f, i) => $"{f.Name}: {architectures[i]}").ToList()));
        }
    }

    private static void ValidateIdentityConsistency(
        IReadOnlyList<AppxManifestDocument> manifests,
        IReadOnlyList<DirectoryInfo> inputFolders,
        List<BundleValidationError> errors)
    {
        ValidateFieldConsistency(manifests, inputFolders, errors, "Identity/@Name",
            m => m.IdentityName ?? string.Empty);
        ValidateFieldConsistency(manifests, inputFolders, errors, "Identity/@Publisher",
            m => m.IdentityPublisher ?? string.Empty);
        ValidateFieldConsistency(manifests, inputFolders, errors, "Identity/@Version",
            m => m.IdentityVersion ?? string.Empty);
    }

    private static void ValidateCapabilitiesConsistency(
        IReadOnlyList<AppxManifestDocument> manifests,
        IReadOnlyList<DirectoryInfo> inputFolders,
        List<BundleValidationError> errors)
    {
        var capSets = manifests.Select(m => GetCanonicalCapabilities(m)).ToList();
        var reference = capSets[0];

        for (int i = 1; i < capSets.Count; i++)
        {
            if (!reference.SetEquals(capSets[i]))
            {
                var sliceValues = capSets.Select((caps, idx) =>
                    $"{inputFolders[idx].Name}: [{string.Join(", ", caps.OrderBy(c => c))}]").ToList();
                errors.Add(new BundleValidationError(
                    "Capabilities",
                    "Capabilities differ across slices. All slices in a bundle must declare the same capabilities.",
                    sliceValues));
                break;
            }
        }
    }

    private static void ValidateDependenciesConsistency(
        IReadOnlyList<AppxManifestDocument> manifests,
        IReadOnlyList<DirectoryInfo> inputFolders,
        List<BundleValidationError> errors)
    {
        // Validate PackageDependency (Name + MinVersion)
        var depSets = manifests.Select(m => GetCanonicalPackageDependencies(m)).ToList();
        var reference = depSets[0];

        for (int i = 1; i < depSets.Count; i++)
        {
            if (!reference.SetEquals(depSets[i]))
            {
                var sliceValues = depSets.Select((deps, idx) =>
                    $"{inputFolders[idx].Name}: [{string.Join(", ", deps.OrderBy(d => d))}]").ToList();
                errors.Add(new BundleValidationError(
                    "Dependencies/PackageDependency",
                    "Package dependencies differ across slices. Ensure all slices use the same dependency versions (especially Windows App SDK runtime version for --self-contained).",
                    sliceValues));
                break;
            }
        }

        // Validate TargetDeviceFamily (MinVersion + MaxVersionTested)
        var tdfSets = manifests.Select(m => GetCanonicalTargetDeviceFamilies(m)).ToList();
        var tdfReference = tdfSets[0];

        for (int i = 1; i < tdfSets.Count; i++)
        {
            if (!tdfReference.SetEquals(tdfSets[i]))
            {
                var sliceValues = tdfSets.Select((tdfs, idx) =>
                    $"{inputFolders[idx].Name}: [{string.Join(", ", tdfs.OrderBy(t => t))}]").ToList();
                errors.Add(new BundleValidationError(
                    "Dependencies/TargetDeviceFamily",
                    "TargetDeviceFamily declarations differ across slices.",
                    sliceValues));
                break;
            }
        }
    }

    private static void ValidateApplicationsConsistency(
        IReadOnlyList<AppxManifestDocument> manifests,
        IReadOnlyList<DirectoryInfo> inputFolders,
        List<BundleValidationError> errors)
    {
        var appSets = manifests.Select(m => GetCanonicalApplicationIds(m)).ToList();
        var reference = appSets[0];

        for (int i = 1; i < appSets.Count; i++)
        {
            if (!reference.SetEquals(appSets[i]))
            {
                var sliceValues = appSets.Select((apps, idx) =>
                    $"{inputFolders[idx].Name}: [{string.Join(", ", apps.OrderBy(a => a))}]").ToList();
                errors.Add(new BundleValidationError(
                    "Applications",
                    "Application declarations differ across slices. All slices must declare the same Application Ids.",
                    sliceValues));
                break;
            }
        }
    }

    private static void ValidateFieldConsistency(
        IReadOnlyList<AppxManifestDocument> manifests,
        IReadOnlyList<DirectoryInfo> inputFolders,
        List<BundleValidationError> errors,
        string fieldName,
        Func<AppxManifestDocument, string> extractor)
    {
        var values = manifests.Select(extractor).ToList();
        var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (distinct.Count > 1)
        {
            var sliceValues = values.Select((v, idx) => $"{inputFolders[idx].Name}: \"{v}\"").ToList();
            errors.Add(new BundleValidationError(
                fieldName,
                $"{fieldName} differs across slices. All slices in a bundle must have the same {fieldName}.",
                sliceValues));
        }
    }

    private static HashSet<string> GetCanonicalCapabilities(AppxManifestDocument manifest)
    {
        var capsElement = manifest.GetCapabilitiesElement();
        if (capsElement == null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return capsElement.Elements()
            .Select(e => $"{e.Name.LocalName}:{e.Attribute("Name")?.Value ?? e.Name.LocalName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetCanonicalPackageDependencies(AppxManifestDocument manifest)
    {
        var depsElement = manifest.GetDependenciesElement();
        if (depsElement == null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return depsElement.Elements(AppxManifestDocument.DefaultNs + "PackageDependency")
            .Select(e => $"{e.Attribute("Name")?.Value}@{e.Attribute("MinVersion")?.Value}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetCanonicalTargetDeviceFamilies(AppxManifestDocument manifest)
    {
        var depsElement = manifest.GetDependenciesElement();
        if (depsElement == null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return depsElement.Elements(AppxManifestDocument.DefaultNs + "TargetDeviceFamily")
            .Select(e => $"{e.Attribute("Name")?.Value}|{e.Attribute("MinVersion")?.Value}|{e.Attribute("MaxVersionTested")?.Value}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetCanonicalApplicationIds(AppxManifestDocument manifest)
    {
        var appsElement = manifest.Document.Root?.Element(AppxManifestDocument.DefaultNs + "Applications");
        if (appsElement == null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return appsElement.Elements(AppxManifestDocument.DefaultNs + "Application")
            .Select(e => e.Attribute("Id")?.Value ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

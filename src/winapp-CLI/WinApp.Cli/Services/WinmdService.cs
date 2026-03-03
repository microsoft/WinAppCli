// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WinApp.Cli.Services;

internal sealed class WinmdService(ILogger<WinmdService> logger) : IWinmdService
{
    /// <summary>
    /// WinRT metadata attribute names that indicate a class needs an activation factory
    /// (and therefore an InProcessServer / ActivatableClass manifest entry).
    /// </summary>
    private static readonly HashSet<string> ActivationAttributeNames =
    [
        "ActivatableAttribute",   // class supports direct/factory activation
        "StaticAttribute",        // class exposes static factory interfaces
        "ComposableAttribute"     // class supports composition (subclassing)
    ];

    /// <inheritdoc/>
    public IReadOnlyList<string> GetActivatableClasses(FileInfo winmdPath)
    {
        if (!winmdPath.Exists)
        {
            logger.LogDebug("Winmd file not found: {Path}", winmdPath.FullName);
            return [];
        }

        try
        {
            return ReadActivatableClasses(winmdPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            logger.LogWarning("Skipping invalid or unreadable winmd file {Path}: {Message}", winmdPath.FullName, ex.Message);
            return [];
        }
    }

    private static List<string> ReadActivatableClasses(FileInfo winmdPath)
    {
        var classes = new List<string>();

        using var stream = File.OpenRead(winmdPath.FullName);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
        {
            return [];
        }

        var reader = peReader.GetMetadataReader();

        // Build a set of TypeDefinition handles that have activation attributes
        var activatableTypeHandles = new HashSet<TypeDefinitionHandle>();
        foreach (var caHandle in reader.CustomAttributes)
        {
            var ca = reader.GetCustomAttribute(caHandle);
            if (ca.Parent.Kind != HandleKind.TypeDefinition)
            {
                continue;
            }

            var attrName = GetAttributeName(reader, ca);
            if (attrName != null && ActivationAttributeNames.Contains(attrName))
            {
                activatableTypeHandles.Add((TypeDefinitionHandle)ca.Parent);
            }
        }

        foreach (var typeDefHandle in reader.TypeDefinitions)
        {
            // Only include types that have at least one activation attribute
            if (!activatableTypeHandles.Contains(typeDefHandle))
            {
                continue;
            }

            var typeDef = reader.GetTypeDefinition(typeDefHandle);

            // Skip non-public types
            var visibility = typeDef.Attributes & System.Reflection.TypeAttributes.VisibilityMask;
            if (visibility != System.Reflection.TypeAttributes.Public)
            {
                continue;
            }

            var namespaceName = reader.GetString(typeDef.Namespace);
            var typeName = reader.GetString(typeDef.Name);

            if (string.IsNullOrEmpty(namespaceName))
            {
                continue;
            }

            var fullName = $"{namespaceName}.{typeName}";
            classes.Add(fullName);
        }

        return classes;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WinRTComponent> DiscoverWinRTComponents(
        DirectoryInfo nugetCacheDir,
        Dictionary<string, string> packages,
        string architecture,
        IReadOnlySet<string>? excludePackageNames = null)
    {
        var results = new List<WinRTComponent>();

        foreach (var (packageName, version) in packages)
        {
            // Skip excluded packages
            if (excludePackageNames?.Contains(packageName) == true)
            {
                continue;
            }

            var packageDir = new DirectoryInfo(Path.Combine(
                nugetCacheDir.FullName, packageName.ToLowerInvariant(), version));

            if (!packageDir.Exists)
            {
                continue;
            }

            // Skip packages that have runtimes-framework/package.appxfragment
            // (already handled by the existing WinApp SDK fragment processing)
            var appxFragmentPath = Path.Combine(packageDir.FullName, "runtimes-framework", "package.appxfragment");
            if (File.Exists(appxFragmentPath))
            {
                continue;
            }

            // Find .winmd files in this package
            var winmdFiles = FindWinmdsInPackage(packageDir);
            if (winmdFiles.Count == 0)
            {
                continue;
            }

            // Build a set of candidate implementation DLLs from multiple locations:
            // 1. runtimes/win-{arch}/native/ — native WinRT components (e.g., Win2D)
            // 2. lib/ directories — managed WinRT wrappers (e.g., WebView2)
            var candidateDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var nativeDir = new DirectoryInfo(Path.Combine(
                packageDir.FullName, "runtimes", $"win-{architecture}", "native"));

            if (nativeDir.Exists)
            {
                foreach (var dll in nativeDir.EnumerateFiles("*.dll"))
                {
                    candidateDlls.Add(Path.GetFileNameWithoutExtension(dll.Name));
                }
            }

            // Also check lib/ directories for managed implementation DLLs
            // (e.g., WebView2 ships Microsoft.Web.WebView2.Core.dll in lib/net462/)
            var libDir = new DirectoryInfo(Path.Combine(packageDir.FullName, "lib"));
            if (libDir.Exists)
            {
                foreach (var dll in SafeEnumFiles(libDir, "*.dll", SearchOption.AllDirectories))
                {
                    candidateDlls.Add(Path.GetFileNameWithoutExtension(dll.Name));
                }
            }

            if (candidateDlls.Count == 0)
            {
                continue;
            }

            foreach (var winmd in winmdFiles)
            {
                var winmdStem = Path.GetFileNameWithoutExtension(winmd.Name);

                // Check if there's a DLL with a matching name stem
                if (candidateDlls.Contains(winmdStem))
                {
                    results.Add(new WinRTComponent(winmd, $"{winmdStem}.dll"));
                }
            }
        }

        // Deduplicate by implementation DLL name — multiple TFM directories
        // (e.g. lib/net8.0-..., lib/net10.0-...) may contain the same .winmd.
        // Keep only the first discovered entry for each DLL.
        return results
            .GroupBy(c => c.ImplementationDll, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Finds .winmd files within a single NuGet package directory
    /// using the same search paths as PackageLayoutService.FindWinmds.
    /// </summary>
    private static List<FileInfo> FindWinmdsInPackage(DirectoryInfo packageDir)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Probe known top-level directories directly (NuGet packages have a well-defined layout)
        var metadataDir = new DirectoryInfo(Path.Combine(packageDir.FullName, "metadata"));
        if (metadataDir.Exists)
        {
            AddWinmdFiles(results, metadataDir);
            var v18362 = new DirectoryInfo(Path.Combine(metadataDir.FullName, "10.0.18362.0"));
            AddWinmdFiles(results, v18362);
        }

        // Search in lib/ directory (common location for .winmd files, recurse into TFM subdirs)
        var libDir = new DirectoryInfo(Path.Combine(packageDir.FullName, "lib"));
        if (libDir.Exists)
        {
            foreach (var f in SafeEnumFiles(libDir, "*.winmd", SearchOption.AllDirectories))
            {
                results.Add(f.FullName);
            }
        }

        // Search in References/ directory
        var refDir = new DirectoryInfo(Path.Combine(packageDir.FullName, "References"));
        if (refDir.Exists)
        {
            foreach (var f in SafeEnumFiles(refDir, "*.winmd", SearchOption.AllDirectories))
            {
                results.Add(f.FullName);
            }
        }

        return results.Select(f => new FileInfo(f)).ToList();
    }

    private static void AddWinmdFiles(HashSet<string> results, DirectoryInfo dir)
    {
        foreach (var f in SafeEnumFiles(dir, "*.winmd", SearchOption.TopDirectoryOnly))
        {
            results.Add(f.FullName);
        }
    }

    /// <summary>
    /// Resolves the simple name of the attribute type from a CustomAttribute's constructor handle.
    /// </summary>
    private static string? GetAttributeName(MetadataReader reader, CustomAttribute ca)
    {
        if (ca.Constructor.Kind != HandleKind.MemberReference)
        {
            return null;
        }

        var memberRef = reader.GetMemberReference((MemberReferenceHandle)ca.Constructor);
        if (memberRef.Parent.Kind != HandleKind.TypeReference)
        {
            return null;
        }

        var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
        return reader.GetString(typeRef.Name);
    }

    private static IEnumerable<FileInfo> SafeEnumFiles(DirectoryInfo root, string searchPattern, SearchOption option)
    {
        try { return root.Exists ? root.EnumerateFiles(searchPattern, option) : []; }
        catch { return []; }
    }
}

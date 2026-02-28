// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WinApp.Cli.Services;

internal sealed class WinmdService : IWinmdService
{
    /// <summary>
    /// Packages whose .winmd files are OS/system types and should never
    /// generate activatable class entries. These are compile-time-only references.
    /// </summary>
    private static readonly HashSet<string> ExcludedPackagePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Windows.SDK.CPP",
        "Microsoft.Windows.SDK.Contracts",
        "Microsoft.Windows.SDK.BuildTools",
        "Microsoft.Windows.CppWinRT",
        "Microsoft.Windows.ImplementationLibrary",
    };

    /// <inheritdoc/>
    public IReadOnlyList<string> GetActivatableClasses(FileInfo winmdPath)
    {
        if (!winmdPath.Exists)
        {
            return [];
        }

        var classes = new List<string>();

        using var stream = File.OpenRead(winmdPath.FullName);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
        {
            return [];
        }

        var reader = peReader.GetMetadataReader();

        foreach (var typeDefHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeDefHandle);

            // Skip non-public types
            var visibility = typeDef.Attributes & System.Reflection.TypeAttributes.VisibilityMask;
            if (visibility != System.Reflection.TypeAttributes.Public)
            {
                continue;
            }

            // Skip nested types (they're activated through their parent)
            if (typeDef.IsNested)
            {
                continue;
            }

            // Skip interfaces (Abstract + ClassSemanticsMask == Interface)
            if ((typeDef.Attributes & System.Reflection.TypeAttributes.ClassSemanticsMask) == System.Reflection.TypeAttributes.Interface)
            {
                continue;
            }

            // Skip value types (structs/enums) — not activatable
            var baseTypeHandle = typeDef.BaseType;
            if (!baseTypeHandle.IsNil)
            {
                var baseTypeName = GetFullTypeName(reader, baseTypeHandle);
                if (baseTypeName is "System.ValueType" or "System.Enum" or "System.MulticastDelegate")
                {
                    continue;
                }
            }

            // Skip types with no public constructors and no static/activatable attributes
            // In WinRT, a class without activation factories is typically a static class
            // but we still include it — the runtime needs entries for static classes with
            // factory interfaces too. Only skip if it looks like a pure attribute.
            var baseType = !baseTypeHandle.IsNil ? GetFullTypeName(reader, baseTypeHandle) : null;
            if (baseType == "System.Attribute")
            {
                continue;
            }

            var namespaceName = reader.GetString(typeDef.Namespace);
            var typeName = reader.GetString(typeDef.Name);

            // Skip the synthetic <Module> type
            if (string.IsNullOrEmpty(namespaceName) && typeName == "<Module>")
            {
                continue;
            }

            // Skip types in implementation-detail namespaces
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

            // Skip known SDK/system packages that have .winmd but no activatable components
            if (IsExcludedPackage(packageName))
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

        // Search in metadata/ directories
        foreach (var metadataDir in SafeEnumDirs(packageDir, "metadata"))
        {
            AddWinmdFiles(results, metadataDir);
            var v18362 = new DirectoryInfo(Path.Combine(metadataDir.FullName, "10.0.18362.0"));
            AddWinmdFiles(results, v18362);
        }

        // Search in lib/ directories (common location for .winmd files)
        foreach (var libDir in SafeEnumDirs(packageDir, "lib"))
        {
            // Search all TFM subdirectories for .winmd files
            if (libDir.Exists)
            {
                foreach (var f in SafeEnumFiles(libDir, "*.winmd", SearchOption.AllDirectories))
                {
                    results.Add(f.FullName);
                }
            }
        }

        // Search in References/ directories
        foreach (var refDir in SafeEnumDirs(packageDir, "References"))
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

    private static bool IsExcludedPackage(string packageName)
    {
        foreach (var prefix in ExcludedPackagePrefixes)
        {
            if (packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? GetFullTypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil)
        {
            return null;
        }

        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)handle);
                var ns = reader.GetString(typeRef.Namespace);
                var name = reader.GetString(typeRef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            case HandleKind.TypeDefinition:
                var typeDef = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                var defNs = reader.GetString(typeDef.Namespace);
                var defName = reader.GetString(typeDef.Name);
                return string.IsNullOrEmpty(defNs) ? defName : $"{defNs}.{defName}";

            default:
                return null;
        }
    }

    private static IEnumerable<DirectoryInfo> SafeEnumDirs(DirectoryInfo root, string searchPattern)
    {
        try { return root.Exists ? root.EnumerateDirectories(searchPattern, SearchOption.AllDirectories) : []; }
        catch { return []; }
    }

    private static IEnumerable<FileInfo> SafeEnumFiles(DirectoryInfo root, string searchPattern, SearchOption option)
    {
        try { return root.Exists ? root.EnumerateFiles(searchPattern, option) : []; }
        catch { return []; }
    }
}

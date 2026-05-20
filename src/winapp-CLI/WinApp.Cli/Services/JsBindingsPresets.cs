// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

// Codegen role per winmd.
internal enum WinmdPackageCategory
{
    Emit,     // --winmd
    RefOnly,  // --ref
    Skip,
}

// Winmd / package categorization for JS bindings. Owns the static denylists
// (skip / ref-only) and merging them with user `jsBindings:` overrides from
// winapp.yaml. Shared by JsBindingsWorkspaceService and WinmdsLockfileService.
internal static class JsBindingsPresets
{
    // Built-in denylists; user `jsBindings` overrides layer on top.

    // RefOnly: own classes are undriveable but other packages reference them.
    private static readonly HashSet<string> DefaultRefOnlyPackages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.WindowsAppSDK.InteractiveExperiences",
        };

    // Skip: dropped entirely.
    private static readonly HashSet<string> DefaultSkippedPackages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.WindowsAppSDK.WinUI",
        };

    // User overrides. Precedence: Emit → Skip → RefOnly → Emit (default).
    public sealed class PackageCategoryOverrides
    {
        public IReadOnlyCollection<string>? Skip { get; init; }
        public IReadOnlyCollection<string>? RefOnly { get; init; }
        public IReadOnlyCollection<string>? Emit { get; init; }

        public static readonly PackageCategoryOverrides Empty = new();

        public static PackageCategoryOverrides From(Models.JsBindingsConfig? config)
        {
            if (config is null)
            {
                return Empty;
            }
            return new PackageCategoryOverrides
            {
                Skip = config.SkipPackages.Count > 0
                    ? new HashSet<string>(config.SkipPackages, StringComparer.OrdinalIgnoreCase)
                    : null,
                RefOnly = config.RefOnlyPackages.Count > 0
                    ? new HashSet<string>(config.RefOnlyPackages, StringComparer.OrdinalIgnoreCase)
                    : null,
                Emit = config.EmitPackages.Count > 0
                    ? new HashSet<string>(config.EmitPackages, StringComparer.OrdinalIgnoreCase)
                    : null,
            };
        }
    }

    // Categorize packageId; defaults to Emit.
    public static WinmdPackageCategory ClassifyPackage(
        string packageId,
        PackageCategoryOverrides? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return WinmdPackageCategory.Emit;
        }
        var ov = overrides ?? PackageCategoryOverrides.Empty;

        // Force-emit always wins — lets users opt back in to a denylisted package.
        if (ov.Emit is not null && ov.Emit.Contains(packageId))
        {
            return WinmdPackageCategory.Emit;
        }
        if (DefaultSkippedPackages.Contains(packageId) ||
            (ov.Skip is not null && ov.Skip.Contains(packageId)))
        {
            return WinmdPackageCategory.Skip;
        }
        if (DefaultRefOnlyPackages.Contains(packageId) ||
            (ov.RefOnly is not null && ov.RefOnly.Contains(packageId)))
        {
            return WinmdPackageCategory.RefOnly;
        }
        return WinmdPackageCategory.Emit;
    }

    // Extract package ID from a NuGet-cache winmd path. With `nugetCacheRoot`,
    // returns the child segment of the cache root; without it, scans for a
    // literal "packages" segment. Returns null when neither applies.
    public static string? ExtractPackageIdFromPath(string winmdPath, string? nugetCacheRoot = null)
    {
        if (string.IsNullOrWhiteSpace(winmdPath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(nugetCacheRoot))
        {
            try
            {
                var full = Path.GetFullPath(winmdPath);
                var root = Path.GetFullPath(nugetCacheRoot!)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var rootPrefix = root + Path.DirectorySeparatorChar;
                if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = full.Substring(rootPrefix.Length);
                    var firstSep = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                    return firstSep > 0 ? rel.Substring(0, firstSep) : rel;
                }
            }
            catch
            {
                // Fall through to legacy heuristic.
            }
        }

        var segs = winmdPath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length - 1; i++)
        {
            if (segs[i].Equals("packages", StringComparison.OrdinalIgnoreCase))
            {
                return segs[i + 1];
            }
        }
        return null;
    }

    // Result of partitioning a discovered winmd list by category.
    public readonly record struct WinmdPartition(
        IReadOnlyList<FileInfo> Emit,
        IReadOnlyList<FileInfo> RefOnly,
        IReadOnlyList<FileInfo> Skipped);

    // Partition winmds by category. Entries with no extractable package ID
    // default to Emit. When `emitScope` is provided, out-of-scope emit
    // packages are demoted to RefOnly so codegen still sees their metadata
    // for cross-package type resolution. Skip/RefOnly classifications take
    // precedence over scope.
    public static WinmdPartition PartitionByPackageCategory(
        IReadOnlyList<FileInfo> winmds,
        PackageCategoryOverrides? overrides = null,
        string? nugetCacheRoot = null,
        IReadOnlyCollection<string>? emitScope = null)
    {
        HashSet<string>? scope = emitScope is { Count: > 0 }
            ? new HashSet<string>(emitScope, StringComparer.OrdinalIgnoreCase)
            : null;

        var emit = new List<FileInfo>();
        var refOnly = new List<FileInfo>();
        var skipped = new List<FileInfo>();
        foreach (var w in winmds)
        {
            var pkg = ExtractPackageIdFromPath(w.FullName, nugetCacheRoot);
            var cat = pkg is null ? WinmdPackageCategory.Emit : ClassifyPackage(pkg, overrides);

            if (scope is not null
                && cat == WinmdPackageCategory.Emit
                && pkg is not null
                && !scope.Contains(pkg))
            {
                cat = WinmdPackageCategory.RefOnly;
            }

            switch (cat)
            {
                case WinmdPackageCategory.Skip: skipped.Add(w); break;
                case WinmdPackageCategory.RefOnly: refOnly.Add(w); break;
                default: emit.Add(w); break;
            }
        }
        return new WinmdPartition(emit, refOnly, skipped);
    }
}

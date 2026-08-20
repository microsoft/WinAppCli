// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>The kind of a type member surfaced by the API metadata index.</summary>
internal enum MemberKind
{
    Method,
    Property,
    Event,
    Field
}

/// <summary>The kind of a type surfaced by the API metadata index.</summary>
internal enum TypeKind
{
    Class,
    Struct,
    Enum,
    Interface,
    Delegate
}

/// <summary>A single parameter of a method member.</summary>
internal sealed class WinMdParameterInfo
{
    public required string Name { get; init; }

    public required string Type { get; init; }
}

/// <summary>A property, method, event, or field declared on a <see cref="WinMdTypeInfo"/>.</summary>
internal sealed class WinMdMemberInfo
{
    public required string Name { get; init; }

    public required MemberKind Kind { get; init; }

    public required string Signature { get; init; }

    public string? ReturnType { get; init; }

    public List<WinMdParameterInfo>? Parameters { get; init; }

    public string? Description { get; set; }

    public string? DeprecatedMessage { get; set; }
}

/// <summary>A WinRT/managed type parsed from a <c>.winmd</c>/<c>.dll</c> plus its XML-doc description.</summary>
internal sealed class WinMdTypeInfo
{
    public required string Namespace { get; init; }

    public required string Name { get; init; }

    public required string FullName { get; init; }

    public required TypeKind Kind { get; init; }

    public string? BaseType { get; init; }

    public required List<WinMdMemberInfo> Members { get; init; }

    public List<string>? EnumValues { get; init; }

    public required string SourceFile { get; init; }

    public string? Description { get; set; }

    public string? DeprecatedMessage { get; set; }
}

/// <summary>A NuGet/SDK package (or project reference) that carries <c>.winmd</c> metadata and XML docs.</summary>
internal sealed record PackageWithWinMd(string Id, string Version, List<string> WinMdFiles, List<string> XmlDocFiles);

/// <summary>A package reference recorded in a cached <see cref="ProjectManifest"/>.</summary>
internal sealed class ProjectPackageRef
{
    public required string Id { get; init; }

    public required string Version { get; init; }
}

/// <summary>The cached manifest describing a project's resolved metadata packages.</summary>
internal sealed class ProjectManifest
{
    public required string ProjectName { get; init; }

    public required string ProjectDir { get; init; }

    public required string ProjectFile { get; init; }

    public required List<ProjectPackageRef> Packages { get; init; }

    public required string GeneratedAt { get; init; }
}

/// <summary>The <c>meta.json</c> summary written alongside each cached package.</summary>
internal sealed class PackageMeta
{
    /// <summary>
    /// The <see cref="ApiCachePaths.CacheFormatVersion"/> this cache was written
    /// with. A cache recording a different version is rebuilt rather than reused.
    /// </summary>
    public int Format { get; init; }

    public required string PackageId { get; init; }

    public required string Version { get; init; }

    public required List<string> WinMdFiles { get; init; }

    public required int TotalTypes { get; init; }

    public required int TotalMembers { get; init; }

    public required int TotalNamespaces { get; init; }

    /// <summary>
    /// True when at least one of the package's metadata files could not be parsed,
    /// so the indexed type list is known to be partial. Queries use this to avoid
    /// reporting an authoritative "not found" from an index that is missing data.
    /// </summary>
    public bool Incomplete { get; init; }

    /// <summary>Per-file parse diagnostics, present only when <see cref="Incomplete"/>.</summary>
    public List<string>? ParseErrors { get; init; }

    public required string GeneratedAt { get; init; }
}

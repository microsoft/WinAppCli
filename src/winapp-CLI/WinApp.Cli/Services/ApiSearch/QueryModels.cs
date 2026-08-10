// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>The outcome of a <c>find-api</c> query, used to pick the right exit code and message.</summary>
internal enum ApiQueryOutcome
{
    Ok,
    NoProject,
    InvalidInput,
    NotFound,
    NotAnEnum,
}

/// <summary>
/// Wraps a query payload with an <see cref="ApiQueryOutcome"/> and optional message
/// so command handlers can render text/JSON success or a clean error envelope.
/// </summary>
internal readonly record struct ApiQueryResult<T>(ApiQueryOutcome Outcome, string? Message, T? Data)
    where T : class
{
    public bool IsOk => Outcome == ApiQueryOutcome.Ok;

    public static ApiQueryResult<T> Ok(T data) => new(ApiQueryOutcome.Ok, null, data);
    public static ApiQueryResult<T> NoProject(string message) => new(ApiQueryOutcome.NoProject, message, null);
    public static ApiQueryResult<T> InvalidInput(string message) => new(ApiQueryOutcome.InvalidInput, message, null);
    public static ApiQueryResult<T> NotFound(string message) => new(ApiQueryOutcome.NotFound, message, null);
    public static ApiQueryResult<T> NotAnEnum(string message) => new(ApiQueryOutcome.NotAnEnum, message, null);
}

/// <summary>
/// Implemented by every query payload that is answered from a resolved scope, so
/// <c>find-api</c> can state — in text and in <c>--json</c> — whether results came
/// from an indexed project or from the machine-wide Windows SDK. Without this a
/// caller cannot tell a project-scoped answer from an SDK-only one.
/// </summary>
internal interface IApiScopedOutput
{
    /// <summary><c>project</c> when answered from an indexed project, <c>sdk</c> for the machine-wide SDK scope.</summary>
    string? Scope { get; set; }
}

/// <summary>Well-known <see cref="IApiScopedOutput.Scope"/> values.</summary>
internal static class ApiScopeNames
{
    public const string Project = "project";
    public const string Sdk = "sdk";
}

// ---- search ----

/// <summary>A single ranked type/member hit within a namespace group.</summary>
internal sealed class ApiTypeHit
{
    public required string Display { get; init; }

    public required int Score { get; init; }
}

/// <summary>Ranked matches grouped by the namespace they live in.</summary>
internal sealed class ApiNamespaceHit
{
    public required string Namespace { get; init; }

    public required int Score { get; init; }

    public required List<string> Files { get; init; }

    public required List<ApiTypeHit> Matches { get; init; }
}

/// <summary>One candidate for an ambiguous (multi-namespace) type name.</summary>
internal sealed class ApiAmbiguityCandidate
{
    public required string FullName { get; init; }

    public required string Kind { get; init; }

    public required int Score { get; init; }

    public string? Description { get; init; }

    public List<string>? EnumValues { get; init; }
}

/// <summary>A short type name that resolves to multiple namespaces (would cause CS0104).</summary>
internal sealed class ApiAmbiguityGroup
{
    public required string Name { get; init; }

    public required List<ApiAmbiguityCandidate> Candidates { get; init; }
}

/// <summary>The result of a <c>find-api "&lt;query&gt;"</c> search.</summary>
internal sealed class ApiSearchOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required string Query { get; init; }

    public List<ApiAmbiguityGroup>? Ambiguous { get; init; }

    public required List<ApiNamespaceHit> Results { get; init; }
}

// ---- members / check-property ----

/// <summary>A single property/event/method entry in a members listing.</summary>
internal sealed class ApiMemberOutput
{
    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required string Signature { get; init; }

    public string? ReturnType { get; init; }

    public string? Description { get; init; }

    public string? Deprecated { get; init; }

    public string? DeclaringType { get; init; }

    public bool Inherited { get; init; }
}

/// <summary>The result of <c>find-api members &lt;Type&gt;</c>.</summary>
internal sealed class ApiMembersOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required string FullName { get; init; }

    public required string Kind { get; init; }

    public string? Description { get; init; }

    public string? BaseType { get; init; }

    public string? Deprecated { get; init; }

    public required List<ApiMemberOutput> Properties { get; init; }

    public required List<ApiMemberOutput> Events { get; init; }

    public required List<ApiMemberOutput> Methods { get; init; }

    public bool GetForCurrentViewWarning { get; init; }
}

/// <summary>A property found on a different type, used for check-property suggestions.</summary>
internal sealed class ApiCrossTypeMember
{
    public required string TypeName { get; init; }

    public required string Signature { get; init; }

    public string? Description { get; init; }
}

/// <summary>The result of <c>find-api check-property &lt;Type&gt; &lt;Property&gt;</c>.</summary>
internal sealed class ApiCheckPropertyOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required bool Found { get; init; }

    public required string Type { get; init; }

    public required string Property { get; init; }

    public ApiMemberOutput? Match { get; init; }

    public bool Attached { get; init; }

    public string? AttachedInfo { get; init; }

    public required List<ApiMemberOutput> SimilarOnType { get; init; }

    public required List<ApiCrossTypeMember> TypesWithProperty { get; init; }

    public required List<ApiCrossTypeMember> TypesWithSimilar { get; init; }
}

// ---- types / enums / namespaces ----

/// <summary>A type summary line for a namespace listing.</summary>
internal sealed class ApiTypeSummary
{
    public required string FullName { get; init; }

    public required string Kind { get; init; }

    public string? BaseType { get; init; }
}

/// <summary>The result of <c>find-api types &lt;Namespace&gt;</c>.</summary>
internal sealed class ApiTypesOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required string Namespace { get; init; }

    public required List<ApiTypeSummary> Types { get; init; }
}

/// <summary>The result of <c>find-api enums &lt;Type&gt;</c>.</summary>
internal sealed class ApiEnumsOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required string FullName { get; init; }

    public required List<string> Values { get; init; }
}

/// <summary>The result of <c>find-api namespaces</c>.</summary>
internal sealed class ApiNamespacesOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required List<string> Namespaces { get; init; }
}

// ---- packages / stats / projects ----

/// <summary>Per-package index stats for a project.</summary>
internal sealed class ApiPackageSummary
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public int? TotalTypes { get; init; }

    public int? TotalMembers { get; init; }

    /// <summary>One of <c>ok</c>, <c>cache-missing</c>, or <c>meta-unreadable</c>.</summary>
    public required string Status { get; init; }
}

/// <summary>The result of <c>find-api packages</c>.</summary>
internal sealed class ApiPackagesOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required string ProjectName { get; init; }

    public required List<ApiPackageSummary> Packages { get; init; }
}

/// <summary>The result of <c>find-api stats</c>.</summary>
internal sealed class ApiStatsOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    public required string ProjectName { get; init; }

    public required int Packages { get; init; }

    public required int Namespaces { get; init; }

    public required int Types { get; init; }

    public required int Members { get; init; }

    public required int WinMdFiles { get; init; }
}

/// <summary>A cached project summary.</summary>
internal sealed class ApiProjectSummary
{
    public required string Name { get; init; }

    public required int PackageCount { get; init; }
}

/// <summary>The result of <c>find-api projects</c>.</summary>
internal sealed class ApiProjectsOutput
{
    public required List<ApiProjectSummary> Projects { get; init; }
}

/// <summary>The result of <c>find-api refresh</c> (a re-scan of the project's metadata).</summary>
internal sealed class ApiRefreshOutput
{
    public required int ProjectsProcessed { get; init; }

    public required int PackagesParsed { get; init; }

    public required int PackagesReused { get; init; }

    public required List<string> ProjectNames { get; init; }
}

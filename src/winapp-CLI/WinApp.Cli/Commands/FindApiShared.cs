// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

/// <summary>
/// Shared plumbing for the <c>winapp find-api</c> verb family: the common
/// <c>--project-dir</c>/<c>--project</c> scope options, the outcome→exit-code +
/// text/JSON emit path (with usage telemetry), and the console renderers that
/// reproduce the standalone tool's output. Rendering uses
/// <see cref="IAnsiConsole.WriteLine(string)"/> for content lines so type names,
/// signatures, and generics with angle brackets stay literal and copy-pasteable.
/// </summary>
internal static class FindApiShared
{
    public static Option<string?> CreateProjectDirOption() => new("--project-dir")
    {
        Description = "Project directory to query (defaults to the current directory). Used to locate the indexed project.",
    };

    public static Option<string?> CreateProjectOption() => new("--project")
    {
        Description = "Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name).",
    };

    public static ApiRequestScope ReadScope(ParseResult parseResult, Option<string?> projectDir, Option<string?> project) =>
        new(parseResult.GetValue(projectDir), parseResult.GetValue(project));

    /// <summary>
    /// Render an <see cref="ApiQueryResult{T}"/>: a clean error envelope for a
    /// non-Ok outcome, otherwise JSON (source-gen) or the text renderer, mapping to
    /// an exit code and emitting one bounded usage event.
    /// </summary>
    public static int Emit<T>(
        IAnsiConsole console,
        bool json,
        string verb,
        ApiQueryResult<T> result,
        JsonTypeInfo<T> jsonType,
        Action<T> renderText,
        Func<T, (int Exit, int Count, bool Found)> summarize)
        where T : class
    {
        if (!result.IsOk)
        {
            FindApiUsageEvent.Log(verb, json, resultCount: 0, found: false);
            return Fail(console, json, result.Message ?? "The find-api query failed.");
        }

        T data = result.Data!;
        (int exit, int count, bool found) = summarize(data);
        FindApiUsageEvent.Log(verb, json, count, found);

        if (json)
        {
            console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(data, jsonType));
        }
        else
        {
            renderText(data);
        }
        return exit;
    }

    /// <summary>
    /// Emit an unwrapped payload (verbs like <c>projects</c>/<c>refresh</c> that
    /// always succeed once reached) as JSON or text, with one usage event.
    /// </summary>
    public static int EmitRaw<T>(
        IAnsiConsole console,
        bool json,
        string verb,
        T data,
        JsonTypeInfo<T> jsonType,
        Action<T> renderText,
        Func<T, (int Exit, int Count, bool Found)> summarize)
        where T : class
    {
        (int exit, int count, bool found) = summarize(data);
        FindApiUsageEvent.Log(verb, json, count, found);
        if (json)
        {
            console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(data, jsonType));
        }
        else
        {
            renderText(data);
        }
        return exit;
    }

    public static int Fail(IAnsiConsole console, bool json, string message)
    {
        if (json)
        {
            return JsonErrorOutput.Write(console, message);
        }
        console.MarkupLineInterpolated($"[red]{UiSymbols.Error} {message}[/]");
        return 1;
    }

    // ---- text renderers (mirror the standalone tool's plain output) ----

    public static void RenderSearch(IAnsiConsole console, ApiSearchOutput output)
    {
        if (output.Ambiguous is { Count: > 0 })
        {
            foreach (ApiAmbiguityGroup group in output.Ambiguous)
            {
                console.WriteLine($"\u26a0\ufe0f AMBIGUOUS \u2014 '{group.Name}' found in multiple namespaces:");
                console.WriteLine();
                foreach (ApiAmbiguityCandidate candidate in group.Candidates)
                {
                    console.WriteLine($"  [{candidate.Score}] {candidate.FullName} ({candidate.Kind})");
                    if (candidate.EnumValues is { Count: > 0 })
                    {
                        string preview = string.Join(", ", candidate.EnumValues.Take(6));
                        if (candidate.EnumValues.Count > 6)
                        {
                            preview += ", ...";
                        }
                        console.WriteLine($"        Values: {preview}");
                    }
                    if (!string.IsNullOrEmpty(candidate.Description))
                    {
                        console.WriteLine($"        {candidate.Description}");
                    }
                }
                console.WriteLine();
                console.WriteLine("  Use the fully-qualified name to avoid CS0104.");
                console.WriteLine();
            }
            return;
        }

        if (output.Results.Count == 0)
        {
            console.WriteLine($"No results found for: {output.Query}");
            return;
        }

        foreach (ApiNamespaceHit ns in output.Results)
        {
            console.WriteLine($"[{ns.Score}] {ns.Namespace}");
            foreach (string file in ns.Files)
            {
                console.WriteLine($"    File: {file}");
            }
            foreach (ApiTypeHit match in ns.Matches)
            {
                console.WriteLine($"    {match.Display}");
            }
            console.WriteLine();
        }
    }

    public static void RenderMembers(IAnsiConsole console, ApiMembersOutput output)
    {
        string deprecatedPrefix = output.Deprecated is not null ? "\U0001f6ab " : "";
        console.WriteLine($"{deprecatedPrefix}{output.Kind} {output.FullName}");
        if (output.Deprecated is not null)
        {
            console.WriteLine($"  {output.Deprecated}");
        }
        if (!string.IsNullOrEmpty(output.Description))
        {
            console.WriteLine($"  {output.Description}");
        }
        if (!string.IsNullOrEmpty(output.BaseType))
        {
            console.WriteLine($"  Extends: {output.BaseType}");
        }
        console.WriteLine();

        WriteMemberGroup(console, "Properties:", output.Properties);
        WriteMemberGroup(console, "Events:", output.Events);
        WriteMemberGroup(console, "Methods:", output.Methods);

        if (output.GetForCurrentViewWarning)
        {
            console.WriteLine("  \u26a0\ufe0f GetForCurrentView() requires a CoreWindow (UWP). Desktop WinUI 3 apps");
            console.WriteLine("     may need COM interop (e.g., IInitializeWithWindow, IDataTransferManagerInterop).");
            console.WriteLine();
        }
    }

    private static void WriteMemberGroup(IAnsiConsole console, string heading, List<ApiMemberOutput> members)
    {
        if (members.Count == 0)
        {
            return;
        }
        console.WriteLine($"  {heading}");
        foreach (ApiMemberOutput member in members)
        {
            WriteMember(console, member, "    ");
        }
        console.WriteLine();
    }

    private static void WriteMember(IAnsiConsole console, ApiMemberOutput member, string indent)
    {
        string deprecatedPrefix = member.Deprecated is not null ? "\U0001f6ab " : "";
        string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
        string suffix = member is { Inherited: true, DeclaringType: not null } ? $"  (from {member.DeclaringType})" : "";
        string text = member.Kind == nameof(MemberKind.Event) ? member.Name : member.Signature;
        console.WriteLine($"{indent}{deprecatedPrefix}{text}{desc}{suffix}");
        if (member.Deprecated is not null)
        {
            console.WriteLine($"{indent}  \u21b3 Deprecated: {member.Deprecated}");
        }
    }

    public static void RenderCheckProperty(IAnsiConsole console, ApiCheckPropertyOutput output)
    {
        if (output.Found && output.Match is not null)
        {
            string inherited = output.Match is { Inherited: true, DeclaringType: not null } ? $"  (from {output.Match.DeclaringType})" : "";
            string desc = output.Match.Description is not null ? $" \u2014 {output.Match.Description}" : "";
            console.WriteLine($"\u2705 {output.Type}.{output.Match.Name}");
            console.WriteLine($"   {output.Match.Signature}{inherited}{desc}");
            return;
        }
        if (output is { Attached: true, AttachedInfo: not null })
        {
            console.WriteLine($"\u2705 {output.Type}.{output.Property} (attached)");
            console.WriteLine($"   {output.AttachedInfo}");
            return;
        }

        console.WriteLine($"\u274c {output.Type} does not have property '{output.Property}'");
        console.WriteLine();
        if (output.SimilarOnType.Count > 0)
        {
            console.WriteLine($"  Similar {output.Type} properties:");
            foreach (ApiMemberOutput member in output.SimilarOnType)
            {
                string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
                console.WriteLine($"    {member.Signature}{desc}");
            }
            console.WriteLine();
        }
        if (output.TypesWithProperty.Count > 0)
        {
            console.WriteLine($"  Types that have a '{output.Property}' property:");
            foreach (ApiCrossTypeMember member in output.TypesWithProperty)
            {
                string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
                console.WriteLine($"    {member.TypeName}.{member.Signature}{desc}");
            }
            console.WriteLine();
        }
        if (output.TypesWithSimilar.Count > 0)
        {
            console.WriteLine("  Types with a similar property:");
            foreach (ApiCrossTypeMember member in output.TypesWithSimilar)
            {
                string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
                console.WriteLine($"    {member.TypeName}.{member.Signature}{desc}");
            }
            console.WriteLine();
        }
    }

    public static void RenderTypes(IAnsiConsole console, ApiTypesOutput output)
    {
        foreach (ApiTypeSummary type in output.Types)
        {
            string baseType = string.IsNullOrEmpty(type.BaseType) ? "" : $" : {type.BaseType}";
            console.WriteLine($"{type.Kind} {type.FullName}{baseType}");
        }
    }

    public static void RenderEnums(IAnsiConsole console, ApiEnumsOutput output)
    {
        console.WriteLine($"Enum {output.FullName}");
        if (output.Values.Count == 0)
        {
            console.WriteLine("  (no values)");
            return;
        }
        foreach (string value in output.Values)
        {
            console.WriteLine($"  {value}");
        }
    }

    public static void RenderNamespaces(IAnsiConsole console, ApiNamespacesOutput output)
    {
        foreach (string ns in output.Namespaces)
        {
            console.WriteLine(ns);
        }
    }

    public static void RenderPackages(IAnsiConsole console, ApiPackagesOutput output)
    {
        console.WriteLine($"Packages for project '{output.ProjectName}' ({output.Packages.Count}):");
        foreach (ApiPackageSummary package in output.Packages)
        {
            string detail = package.Status switch
            {
                "ok" => $"{package.TotalTypes} types, {package.TotalMembers} members",
                "meta-unreadable" => "(meta unreadable)",
                _ => "(cache missing)",
            };
            console.WriteLine($"  {package.Id}@{package.Version} -- {detail}");
        }
    }

    public static void RenderStats(IAnsiConsole console, ApiStatsOutput output)
    {
        console.WriteLine($"WinMD Index Statistics -- {output.ProjectName}");
        console.WriteLine("======================================");
        console.WriteLine($"  Packages:   {output.Packages}");
        console.WriteLine($"  Namespaces: {output.Namespaces} (may overlap across packages)");
        console.WriteLine($"  Types:      {output.Types}");
        console.WriteLine($"  Members:    {output.Members}");
        console.WriteLine($"  WinMD files: {output.WinMdFiles}");
    }

    public static void RenderProjects(IAnsiConsole console, ApiProjectsOutput output)
    {
        if (output.Projects.Count == 0)
        {
            console.WriteLine("No projects indexed.");
            return;
        }
        console.WriteLine($"Indexed projects ({output.Projects.Count}):");
        foreach (ApiProjectSummary project in output.Projects)
        {
            console.WriteLine($"  {project.Name} ({project.PackageCount} package(s))");
        }
    }

    public static void RenderRefresh(IAnsiConsole console, ApiRefreshOutput output)
    {
        if (output.ProjectsProcessed == 0)
        {
            console.WriteLine("No projects with API metadata were found to index.");
            return;
        }
        string names = output.ProjectNames.Count > 0 ? $": {string.Join(", ", output.ProjectNames)}" : "";
        console.WriteLine($"Indexed {output.ProjectsProcessed} project(s){names}.");
        console.WriteLine($"  Packages parsed: {output.PackagesParsed}, reused from cache: {output.PackagesReused}.");
    }
}

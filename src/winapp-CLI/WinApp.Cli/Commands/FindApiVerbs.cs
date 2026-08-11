// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary><c>winapp find-api members &lt;Type&gt;</c> — list a type's properties, events, and methods.</summary>
internal sealed class FindApiMembersCommand : Command, IShortDescription
{
    public string ShortDescription => "List a type's properties, events, and methods";

    public static Argument<string?> TypeArgument { get; } = new("type")
    {
        Description = "The type to inspect. Accepts a short name (NavigationView) or a fully-qualified name (Microsoft.UI.Xaml.Controls.NavigationView).",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<string?> FilterOption { get; } = new("--filter")
    {
        Description = "Only list members whose name contains this text (case-insensitive), e.g. --filter background. Totals for the unfiltered type are still reported.",
    };

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiMembersCommand()
        : base("members", "List the properties, events, and methods of a type (with XML-doc descriptions and inherited members), resolved from the project's indexed API metadata.")
    {
        Arguments.Add(TypeArgument);
        Options.Add(FilterOption);
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? type = parseResult.GetValue(TypeArgument);
            if (string.IsNullOrWhiteSpace(type))
            {
                return FindApiShared.Fail(console, json, "A type name is required, e.g. winapp find-api members NavigationView.");
            }

            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);
            var result = service.Members(type, scope, parseResult.GetValue(FilterOption));
            return FindApiShared.Emit(
                console, json, "members", result, WinAppJsonContext.Default.ApiMembersOutput,
                data => FindApiShared.RenderMembers(console, data),
                data => (0, data.Properties.Count + data.Events.Count + data.Methods.Count, true));
        }
    }
}

/// <summary><c>winapp find-api check-property &lt;Type&gt; &lt;Property&gt;</c> — validate a property exists on a type.</summary>
internal sealed class FindApiCheckPropertyCommand : Command, IShortDescription
{
    public string ShortDescription => "Validate that a property exists on a type";

    public static Argument<string?> TypeArgument { get; } = new("type")
    {
        Description = "The type to check.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> PropertyArgument { get; } = new("property")
    {
        Description = "The property name to validate on the type.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiCheckPropertyCommand()
        : base("check-property", "Validate that a property exists on a type before you write XAML/code against it. On a miss, suggests similar properties on the type, attached-property forms, and other types that declare the property. Exits non-zero when the property does not exist.")
    {
        Arguments.Add(TypeArgument);
        Arguments.Add(PropertyArgument);
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? type = parseResult.GetValue(TypeArgument);
            string? property = parseResult.GetValue(PropertyArgument);
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(property))
            {
                return FindApiShared.Fail(console, json, "Usage: winapp find-api check-property <Type> <Property>.");
            }

            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);
            var result = service.CheckProperty(type, property, scope);
            return FindApiShared.Emit(
                console, json, "check-property", result, WinAppJsonContext.Default.ApiCheckPropertyOutput,
                data => FindApiShared.RenderCheckProperty(console, data),
                data => (data.Found ? 0 : 1, 0, data.Found));
        }
    }
}

/// <summary><c>winapp find-api types &lt;Namespace&gt;</c> — list the types in a namespace.</summary>
internal sealed class FindApiTypesCommand : Command, IShortDescription
{
    public string ShortDescription => "List the types declared in a namespace";

    public static Argument<string?> NamespaceArgument { get; } = new("namespace")
    {
        Description = "The namespace to list, e.g. Microsoft.UI.Xaml.Controls.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiTypesCommand()
        : base("types", "List the types declared in a namespace (class/struct/enum/interface/delegate) with their base types.")
    {
        Arguments.Add(NamespaceArgument);
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? ns = parseResult.GetValue(NamespaceArgument);
            if (string.IsNullOrWhiteSpace(ns))
            {
                return FindApiShared.Fail(console, json, "A namespace is required, e.g. winapp find-api types Microsoft.UI.Xaml.Controls.");
            }

            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);
            var result = service.Types(ns, scope);
            return FindApiShared.Emit(
                console, json, "types", result, WinAppJsonContext.Default.ApiTypesOutput,
                data => FindApiShared.RenderTypes(console, data),
                data => (0, data.Types.Count, true));
        }
    }
}

/// <summary><c>winapp find-api enums &lt;Type&gt;</c> — list an enum's values.</summary>
internal sealed class FindApiEnumsCommand : Command, IShortDescription
{
    public string ShortDescription => "List the values of an enum type";

    public static Argument<string?> TypeArgument { get; } = new("type")
    {
        Description = "The enum type to list, e.g. Symbol or Microsoft.UI.Xaml.Controls.Symbol.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<string?> FilterOption { get; } = new("--filter")
    {
        Description = "Only list values whose name contains this text (case-insensitive), e.g. --filter folder. The unfiltered total is still reported.",
    };

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiEnumsCommand()
        : base("enums", "List the values of an enum type. Exits non-zero when the type exists but is not an enum.")
    {
        Arguments.Add(TypeArgument);
        Options.Add(FilterOption);
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? type = parseResult.GetValue(TypeArgument);
            if (string.IsNullOrWhiteSpace(type))
            {
                return FindApiShared.Fail(console, json, "An enum type name is required, e.g. winapp find-api enums Symbol.");
            }

            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);
            var result = service.Enums(type, scope, parseResult.GetValue(FilterOption));
            return FindApiShared.Emit(
                console, json, "enums", result, WinAppJsonContext.Default.ApiEnumsOutput,
                data => FindApiShared.RenderEnums(console, data),
                data => (0, data.Values.Count, true));
        }
    }
}

/// <summary><c>winapp find-api namespaces [--filter &lt;prefix&gt;]</c> — list indexed namespaces.</summary>
internal sealed class FindApiNamespacesCommand : Command, IShortDescription
{
    public string ShortDescription => "List the namespaces available to the project";

    public static Option<string?> FilterOption { get; } = new("--filter")
    {
        Description = "Only list namespaces starting with this prefix, e.g. --filter Microsoft.UI.",
    };

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiNamespacesCommand()
        : base("namespaces", "List the namespaces available to the project across its indexed API metadata, optionally filtered by prefix.")
    {
        Options.Add(FilterOption);
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? filter = parseResult.GetValue(FilterOption);
            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);
            var result = service.Namespaces(filter, scope);
            return FindApiShared.Emit(
                console, json, "namespaces", result, WinAppJsonContext.Default.ApiNamespacesOutput,
                data => FindApiShared.RenderNamespaces(console, data),
                data => (0, data.Namespaces.Count, true));
        }
    }
}

/// <summary><c>winapp find-api packages</c> — list the indexed packages for a project.</summary>
internal sealed class FindApiPackagesCommand : Command, IShortDescription
{
    public string ShortDescription => "List the indexed metadata packages for a project";

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiPackagesCommand()
        : base("packages", "List the NuGet/SDK packages whose API metadata is indexed for a project, with per-package type and member counts.")
    {
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);
            var result = service.Packages(scope);
            return FindApiShared.Emit(
                console, json, "packages", result, WinAppJsonContext.Default.ApiPackagesOutput,
                data => FindApiShared.RenderPackages(console, data),
                data => (0, data.Packages.Count, true));
        }
    }
}

/// <summary><c>winapp find-api stats</c> — show aggregate index statistics for a project.</summary>
internal sealed class FindApiStatsCommand : Command, IShortDescription
{
    public string ShortDescription => "Show aggregate API-index statistics for a project";

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiStatsCommand()
        : base("stats", "Show aggregate statistics for a project's API index: package, namespace, type, member, and .winmd file counts.")
    {
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);
            var result = service.Stats(scope);
            return FindApiShared.Emit(
                console, json, "stats", result, WinAppJsonContext.Default.ApiStatsOutput,
                data => FindApiShared.RenderStats(console, data),
                data => (0, data.Types, true));
        }
    }
}

/// <summary><c>winapp find-api projects</c> — list all indexed projects.</summary>
internal sealed class FindApiProjectsCommand : Command, IShortDescription
{
    public string ShortDescription => "List all indexed projects";

    public FindApiProjectsCommand()
        : base("projects", "List every project that currently has an API index in the shared cache, with the number of packages indexed for each.")
    {
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var data = service.Projects();
            return FindApiShared.EmitRaw(
                console, json, "projects", data, WinAppJsonContext.Default.ApiProjectsOutput,
                d => FindApiShared.RenderProjects(console, d),
                d => (0, d.Projects.Count, d.Projects.Count > 0));
        }
    }
}

/// <summary><c>winapp find-api refresh</c> — rebuild the API index for a project.</summary>
internal sealed class FindApiRefreshCommand : Command, IShortDescription
{
    public string ShortDescription => "Rebuild the API index for a project";

    public static Option<bool> ScanOption { get; } = new("--scan")
    {
        Description = "Recursively discover and index every project under the directory instead of just the top-level project(s).",
    };

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();
    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiRefreshCommand()
        : base("refresh", "Rebuild the API metadata index for a project from its restored packages. Runs automatically when a project is restored; run it manually to force a re-index or to index a project for the first time.")
    {
        Options.Add(ScanOption);
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            bool quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            bool scan = parseResult.GetValue(ScanOption);
            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);

            // Progress prints in text mode only; --json and --quiet suppress it so
            // scripted/quiet callers get a clean payload on stdout.
            Action<string>? onProgress = (json || quiet) ? null : msg => console.MarkupLineInterpolated($"[grey]{msg}[/]");
            // An explicit refresh forces a full rebuild (ignoring reused caches).
            var data = service.Refresh(scope, scan, onProgress, force: true);
            return FindApiShared.EmitRaw(
                console, json, "refresh", data, WinAppJsonContext.Default.ApiRefreshOutput,
                d => FindApiShared.RenderRefresh(console, d),
                d => (d.ProjectsProcessed > 0 ? 0 : 1, d.ProjectsProcessed, d.ProjectsProcessed > 0));
        }
    }
}

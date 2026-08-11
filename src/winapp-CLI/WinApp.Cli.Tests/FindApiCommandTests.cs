// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// A hand-rolled <see cref="IApiMetadataService"/> that records what each verb was
/// called with and returns canned results, so the command layer can be tested for
/// argument parsing, routing, exit codes, and JSON shape without a real cache.
/// Special query/property sentinels drive the non-happy paths.
/// </summary>
internal sealed class FakeApiMetadataService : IApiMetadataService
{
    public const string EmptyQuery = "__empty__";
    public const string NoProjectQuery = "__noproject__";
    public const string MissingProperty = "__missing__";

    public bool SearchCalled { get; private set; }
    public string? LastSearchQuery { get; private set; }
    public int LastMax { get; private set; }
    public string? LastMembersType { get; private set; }
    public string? LastMembersFilter { get; private set; }
    public string? LastEnumsFilter { get; private set; }
    public string? LastCheckType { get; private set; }
    public string? LastCheckProperty { get; private set; }
    public ApiRequestScope LastScope { get; private set; }
    public bool LastRefreshForce { get; private set; }

    public ApiQueryResult<ApiSearchOutput> Search(string query, int maxResults, ApiRequestScope scope)
    {
        SearchCalled = true;
        LastSearchQuery = query;
        LastMax = maxResults;
        LastScope = scope;

        if (query == NoProjectQuery)
        {
            return ApiQueryResult<ApiSearchOutput>.NoProject("No indexed API metadata was found for this project.");
        }

        var results = query == EmptyQuery
            ? new List<ApiNamespaceHit>()
            : new List<ApiNamespaceHit>
            {
                new()
                {
                    Namespace = "Test.Ns",
                    Score = 100,
                    Files = new List<string>(),
                    Matches = new List<ApiTypeHit> { new() { Display = "Class Test.Ns.Foo", Score = 100 } },
                },
            };
        return ApiQueryResult<ApiSearchOutput>.Ok(new ApiSearchOutput { Query = query, Ambiguous = null, Results = results });
    }

    public ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope, string? filter = null)
    {
        LastMembersType = fullName;
        LastMembersFilter = filter;
        LastScope = scope;
        return ApiQueryResult<ApiMembersOutput>.Ok(new ApiMembersOutput
        {
            FullName = fullName,
            Kind = "Class",
            Properties = new List<ApiMemberOutput>
            {
                new() { Name = "Color", Kind = "Property", Signature = "String Color { get; set; }" },
            },
            Events = new List<ApiMemberOutput>(),
            Methods = new List<ApiMemberOutput>(),
        });
    }

    public ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope)
    {
        LastCheckType = typeName;
        LastCheckProperty = propertyName;
        LastScope = scope;
        bool found = propertyName != MissingProperty;
        return ApiQueryResult<ApiCheckPropertyOutput>.Ok(new ApiCheckPropertyOutput
        {
            Found = found,
            Type = typeName,
            Property = propertyName,
            Match = found ? new ApiMemberOutput { Name = propertyName, Kind = "Property", Signature = $"String {propertyName}" } : null,
            SimilarOnType = new List<ApiMemberOutput>(),
            TypesWithProperty = new List<ApiCrossTypeMember>(),
            TypesWithSimilar = new List<ApiCrossTypeMember>(),
        });
    }

    public ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiTypesOutput>.Ok(new ApiTypesOutput
        {
            Namespace = ns,
            Types = new List<ApiTypeSummary> { new() { FullName = ns + ".Foo", Kind = "Class" } },
        });
    }

    public ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope, string? filter = null)
    {
        LastScope = scope;
        LastEnumsFilter = filter;
        return ApiQueryResult<ApiEnumsOutput>.Ok(new ApiEnumsOutput
        {
            FullName = fullName,
            Values = new List<string> { "One", "Two" },
        });
    }

    public ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiNamespacesOutput>.Ok(new ApiNamespacesOutput
        {
            Namespaces = new List<string> { "Test.Ns" },
        });
    }

    public ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiPackagesOutput>.Ok(new ApiPackagesOutput
        {
            ProjectName = "TestApp",
            Packages = new List<ApiPackageSummary> { new() { Id = "Test.Pkg", Version = "1.0.0", Status = "ok", TotalTypes = 1, TotalMembers = 1 } },
        });
    }

    public ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiStatsOutput>.Ok(new ApiStatsOutput
        {
            ProjectName = "TestApp", Packages = 1, Namespaces = 1, Types = 1, Members = 1, WinMdFiles = 1,
        });
    }

    public ApiProjectsOutput Projects() => new()
    {
        Projects = new List<ApiProjectSummary> { new() { Name = "TestApp", PackageCount = 1 } },
    };

    public ApiRefreshOutput Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false)
    {
        LastScope = scope;
        LastRefreshForce = force;
        return new ApiRefreshOutput
        {
            ProjectsProcessed = 1, PackagesParsed = 1, PackagesReused = 0, ProjectNames = new List<string> { "TestApp" },
        };
    }
}

[TestClass]
public sealed class FindApiCommandTests : BaseCommandTests
{
    private FakeApiMetadataService _fake = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fake = new FakeApiMetadataService();
        return services.AddSingleton<IApiMetadataService>(_fake);
    }

    private FindApiCommand Command => GetRequiredService<FindApiCommand>();

    [TestMethod]
    public async Task BareQuery_RoutesToSearch_ExitsZero()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView"]);

        Assert.AreEqual(0, exit);
        Assert.IsTrue(_fake.SearchCalled);
        Assert.AreEqual("NavigationView", _fake.LastSearchQuery);
    }

    [TestMethod]
    public async Task NoQuery_Fails_WithoutCallingService()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, []);

        Assert.AreEqual(1, exit);
        Assert.IsFalse(_fake.SearchCalled);
    }

    [TestMethod]
    public async Task MaxZero_Fails()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--max", "0"]);

        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task EmptyResults_ExitsOne()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, [FakeApiMetadataService.EmptyQuery]);

        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task NoProject_ExitsOne_AndReportsMessage()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, [FakeApiMetadataService.NoProjectQuery]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "No indexed API metadata");
    }

    [TestMethod]
    public async Task Members_RoutesThroughParent()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "Microsoft.UI.Xaml.Controls.NavigationView"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("Microsoft.UI.Xaml.Controls.NavigationView", _fake.LastMembersType);
    }

    [TestMethod]
    public async Task Members_NoType_Fails()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members"]);

        Assert.AreEqual(1, exit);
        Assert.IsNull(_fake.LastMembersType);
    }

    [TestMethod]
    public async Task Members_FilterOption_FlowsToService()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "NavigationView", "--filter", "background"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("background", _fake.LastMembersFilter);
    }

    [TestMethod]
    public async Task Members_WithoutFilterOption_PassesNull()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "NavigationView"]);

        Assert.AreEqual(0, exit);
        Assert.IsNull(_fake.LastMembersFilter);
    }

    [TestMethod]
    public async Task Enums_FilterOption_FlowsToService()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["enums", "Symbol", "--filter", "folder"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("folder", _fake.LastEnumsFilter);
    }

    [TestMethod]
    public async Task CheckProperty_Present_ExitsZero()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "Button", "Background"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("Button", _fake.LastCheckType);
        Assert.AreEqual("Background", _fake.LastCheckProperty);
    }

    [TestMethod]
    public async Task CheckProperty_Missing_ExitsOne()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "Button", FakeApiMetadataService.MissingProperty]);

        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task Search_Json_EmitsQueryField()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--json"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"query\"");
        StringAssert.Contains(TestAnsiConsole.Output, "NavigationView");
    }

    [TestMethod]
    public async Task ProjectOption_FlowsIntoScope()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--project", "MyApp"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("MyApp", _fake.LastScope.Project);
    }

    [TestMethod]
    public async Task Refresh_ForcesFullRebuild()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["refresh"]);

        Assert.AreEqual(0, exit);
        Assert.IsTrue(_fake.LastRefreshForce);
    }

    [TestMethod]
    public async Task Refresh_ProjectOption_FlowsIntoScope()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["refresh", "--project", "MyApp"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("MyApp", _fake.LastScope.Project);
    }
}

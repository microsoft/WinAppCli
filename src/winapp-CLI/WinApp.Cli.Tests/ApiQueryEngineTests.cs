// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Exercises the read side of the API-metadata engine against a hand-built cache
/// on disk, so the whole query surface is covered without parsing real .winmd
/// files. The cache layout mirrors what <see cref="ApiCacheBuilder"/> writes.
/// </summary>
[TestClass]
public sealed class ApiQueryEngineTests
{
    private string _cacheDir = null!;
    private ProjectManifest _manifest = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"ApiQueryEngineTests_{Guid.NewGuid():N}");
        _manifest = BuildSyntheticCache(_cacheDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [TestMethod]
    public void Search_ByTypeName_ReturnsNamespaceHit()
    {
        var result = ApiQueryEngine.Search("Widget", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Results.Any(r => r.Namespace == "My.Ns"), "Widget lives in My.Ns");
    }

    [TestMethod]
    public void Search_GlobalNamespaceType_DoesNotThrow()
    {
        // Regression: a global-namespace type (FullName == Name) once drove the
        // namespace-prefix substring negative and crashed the ambiguity grouping.
        var result = ApiQueryEngine.Search("GlobalThing", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Results.Count > 0, "the global type should still be found");
    }

    [TestMethod]
    public void Search_EmptyQuery_IsInvalidInput()
    {
        var result = ApiQueryEngine.Search("   ", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
    }

    [TestMethod]
    public void Members_ListsPropertiesAndMethods()
    {
        var result = ApiQueryEngine.Members("My.Ns.Widget", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Properties.Any(p => p.Name == "Color"));
        Assert.IsTrue(result.Data.Methods.Any(m => m.Name == "DoThing"));
    }

    [TestMethod]
    public void Members_ShortName_ResolvesType()
    {
        // Regression (C3): members must accept a short name, matching the help text
        // and the check-property resolution behavior.
        var result = ApiQueryEngine.Members("Widget", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual("My.Ns.Widget", result.Data!.FullName);
    }

    [TestMethod]
    public void Members_UnknownType_IsNotFound()
    {
        var result = ApiQueryEngine.Members("My.Ns.Nope", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public void Enums_ListsValues()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.AreEqual(new[] { "Happy", "Sad" }, result.Data!.Values);
    }

    [TestMethod]
    public void Enums_ShortName_ResolvesType()
    {
        // Regression (C3): enums must accept a short name too.
        var result = ApiQueryEngine.Enums("Mood", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual("My.Ns.Mood", result.Data!.FullName);
    }

    [TestMethod]
    public void Enums_NonEnumType_IsNotAnEnum()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Widget", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.NotAnEnum, result.Outcome);
    }

    [TestMethod]
    public void Enums_Unfiltered_ReportsNoFilterMetadata()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", null, _cacheDir, _manifest);

        // Unfiltered payloads must stay byte-identical to before --filter existed.
        Assert.IsNull(result.Data!.Filter);
        Assert.IsNull(result.Data.TotalValues);
    }

    [TestMethod]
    public void Enums_Filter_NarrowsValuesAndReportsTotal()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", "hap", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.AreEqual(new[] { "Happy" }, result.Data!.Values);
        Assert.AreEqual("hap", result.Data.Filter);
        // The pre-filter total must survive so a caller can tell a narrow view
        // from a genuinely small enum.
        Assert.AreEqual(2, result.Data.TotalValues);
    }

    [TestMethod]
    public void Enums_Filter_IsCaseInsensitiveSubstring()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", "AP", _cacheDir, _manifest);

        CollectionAssert.AreEqual(new[] { "Happy" }, result.Data!.Values);
    }

    [TestMethod]
    public void Enums_Filter_NoMatch_IsStillOkWithTotal()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", "zzz", _cacheDir, _manifest);

        // A filter miss is not a missing type: the outcome stays Ok so callers
        // don't confuse "nothing matched my filter" with "no such enum".
        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(0, result.Data!.Values.Count);
        Assert.AreEqual(2, result.Data.TotalValues);
    }

    [TestMethod]
    public void Members_Filter_NarrowsAllGroupsAndReportsTotals()
    {
        var result = ApiQueryEngine.Members("My.Ns.Widget", "col", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.AreEqual(new[] { "Color" }, result.Data!.Properties.Select(p => p.Name).ToArray());
        Assert.AreEqual(0, result.Data.Methods.Count);
        Assert.AreEqual("col", result.Data.Filter);
        Assert.AreEqual(1, result.Data.TotalProperties);
        Assert.AreEqual(1, result.Data.TotalMethods);
    }

    [TestMethod]
    public void Members_Unfiltered_ReportsNoFilterMetadata()
    {
        var result = ApiQueryEngine.Members("My.Ns.Widget", null, _cacheDir, _manifest);

        Assert.IsNull(result.Data!.Filter);
        Assert.IsNull(result.Data.TotalProperties);
        Assert.IsNull(result.Data.TotalMethods);
    }

    [TestMethod]
    public void Types_ListsTypesInNamespace()
    {
        var result = ApiQueryEngine.Types("My.Ns", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        var names = result.Data!.Types.Select(t => t.FullName).ToList();
        CollectionAssert.Contains(names, "My.Ns.Widget");
        CollectionAssert.Contains(names, "My.Ns.Mood");
        CollectionAssert.Contains(names, "My.Ns.Gadget");
    }

    [TestMethod]
    public void Namespaces_FilterByPrefix()
    {
        var result = ApiQueryEngine.Namespaces("My", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.Contains(result.Data!.Namespaces, "My.Ns");
    }

    [TestMethod]
    public void CheckProperty_Existing_IsFound()
    {
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Color", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Found);
    }

    [TestMethod]
    public void CheckProperty_MethodNameCollision_IsNotFound()
    {
        // Regression (C1): a method whose name matches the queried property must
        // not be reported as a found property. "DoThing" is a method on Widget.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "DoThing", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found);
    }

    [TestMethod]
    public void CheckProperty_Missing_IsNotFoundOnType()
    {
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Bogus", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found);
    }

    [TestMethod]
    public void Stats_AggregatesFromMeta()
    {
        var result = ApiQueryEngine.Stats(_cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(4, result.Data!.Types);
        Assert.AreEqual(2, result.Data.Members);
        Assert.AreEqual(1, result.Data.Packages);
    }

    [TestMethod]
    public void Packages_ReportsOkWithCounts()
    {
        var result = ApiQueryEngine.Packages(_cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        var pkg = result.Data!.Packages.Single();
        Assert.AreEqual("Test.Pkg", pkg.Id);
        Assert.AreEqual("ok", pkg.Status);
        Assert.AreEqual(4, pkg.TotalTypes);
    }

    [TestMethod]
    public void Projects_ListsCachedProjects()
    {
        var result = ApiQueryEngine.Projects(_cacheDir);

        Assert.IsTrue(result.Projects.Any(p => p.Name == "TestApp" && p.PackageCount == 1));
    }

    private static ProjectManifest BuildSyntheticCache(string cacheDir)
    {
        const string packageId = "Test.Pkg";
        const string version = "1.0.0";
        string packageDir = Path.Combine(cacheDir, "packages", packageId, version);
        string typesDir = Path.Combine(packageDir, "types");
        Directory.CreateDirectory(typesDir);

        var myNsTypes = new List<WinMdTypeInfo>
        {
            new()
            {
                Namespace = "My.Ns", Name = "Widget", FullName = "My.Ns.Widget", Kind = TypeKind.Class,
                SourceFile = "test.winmd",
                Members =
                [
                    new WinMdMemberInfo { Name = "Color", Kind = MemberKind.Property, Signature = "String Color { get; set; }" },
                    new WinMdMemberInfo { Name = "DoThing", Kind = MemberKind.Method, Signature = "void DoThing()" },
                ],
            },
            new()
            {
                Namespace = "My.Ns", Name = "Mood", FullName = "My.Ns.Mood", Kind = TypeKind.Enum,
                SourceFile = "test.winmd", Members = [], EnumValues = ["Happy", "Sad"],
            },
            new()
            {
                Namespace = "My.Ns", Name = "Gadget", FullName = "My.Ns.Gadget", Kind = TypeKind.Class,
                SourceFile = "test.winmd", Members = [],
            },
        };

        var globalTypes = new List<WinMdTypeInfo>
        {
            new()
            {
                Namespace = "", Name = "GlobalThing", FullName = "GlobalThing", Kind = TypeKind.Class,
                SourceFile = "test.winmd", Members = [],
            },
        };

        File.WriteAllText(Path.Combine(typesDir, "My_Ns.json"), JsonSerializer.Serialize(myNsTypes, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        File.WriteAllText(Path.Combine(typesDir, "_GlobalNamespace.json"), JsonSerializer.Serialize(globalTypes, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        File.WriteAllText(
            Path.Combine(packageDir, "namespaces.json"),
            JsonSerializer.Serialize(new List<string> { "My.Ns", "_GlobalNamespace" }, ApiSearchJsonContext.Default.ListString));

        var meta = new PackageMeta
        {
            PackageId = packageId, Version = version, WinMdFiles = ["test.winmd"],
            TotalTypes = 4, TotalMembers = 2, TotalNamespaces = 2, GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(packageDir, "meta.json"), JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));

        var manifest = new ProjectManifest
        {
            ProjectName = "TestApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "TestApp.csproj",
            Packages = [new ProjectPackageRef { Id = packageId, Version = version }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(Path.Combine(projectsDir, "TestApp.json"), JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));

        return manifest;
    }
}

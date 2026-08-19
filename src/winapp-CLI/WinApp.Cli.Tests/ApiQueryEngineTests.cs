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
    private static readonly string[] MoodValues = ["Happy", "Sad"];
    private static readonly string[] HappyOnly = ["Happy"];
    private static readonly string[] ColorOnly = ["Color"];
    private static readonly string[] ExpectedAlphaCandidates = ["Dup.Ns.Alpha", "Other.Ns.Alpha"];
    private static readonly string[] DuplicatePackageIds = ["Dup.PkgA", "Dup.PkgB"];

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
        CollectionAssert.AreEqual(MoodValues, result.Data!.Values);
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
        CollectionAssert.AreEqual(HappyOnly, result.Data!.Values);
        Assert.AreEqual("hap", result.Data.Filter);
        // The pre-filter total must survive so a caller can tell a narrow view
        // from a genuinely small enum.
        Assert.AreEqual(2, result.Data.TotalValues);
    }

    [TestMethod]
    public void Enums_Filter_IsCaseInsensitiveSubstring()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", "AP", _cacheDir, _manifest);

        CollectionAssert.AreEqual(HappyOnly, result.Data!.Values);
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
        CollectionAssert.AreEqual(ColorOnly, result.Data!.Properties.Select(p => p.Name).ToArray());
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

    #region Duplicate collapse and ABI projection filtering

    [TestMethod]
    public void Search_SameTypeInSeveralPackages_EmitsEachFullNameOnce()
    {
        // Regression: Search walked every package's type files without a seen-set, so a type
        // shipped by more than one package (the norm — e.g. Microsoft.UI.Xaml.Controls.Button
        // lives in both WinAppSdkRuntime and Microsoft.WindowsAppSDK.WinUI) was scored and
        // emitted once per package. Roughly 42-44% of real ambiguity candidates were dupes.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Alpha", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            ApiAmbiguityGroup group = result.Data!.Ambiguous!.Single(g => g.Name == "Alpha");
            CollectionAssert.AreEquivalent(
                ExpectedAlphaCandidates,
                group.Candidates.Select(c => c.FullName).ToList(),
                "Both packages ship both types, but each fully-qualified name must appear once.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Search_DuplicatePackages_DoNotCrowdOutDistinctMatches()
    {
        // The per-namespace match list is capped at 5. Before the dedupe, copies of one type
        // could consume several of those slots and push genuinely different types out.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Alpha", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            ApiNamespaceHit hit = result.Data!.Results.Single(r => r.Namespace == "Dup.Ns");
            var displays = hit.Matches.Select(m => m.Display).ToList();
            CollectionAssert.AllItemsAreUnique(displays, "A capped match list must not spend slots on duplicates.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Search_AbiProjectionTwins_AreExcludedFromResults()
    {
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Alpha", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsFalse(
                result.Data!.Results.Any(r => r.Namespace.StartsWith("ABI.", StringComparison.Ordinal)),
                "CsWinRT ABI projection namespaces are marshalling internals and must not surface in search.");
            Assert.IsFalse(
                result.Data.Ambiguous!.SelectMany(g => g.Candidates).Any(c => c.FullName.StartsWith("ABI.", StringComparison.Ordinal)),
                "ABI twins must not appear as disambiguation candidates.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Search_TypeWithOnlyAnAbiTwin_IsNotReportedAmbiguous()
    {
        // 'ABI.Solo.Ns' counts as a distinct namespace prefix, so before the filter a type
        // living in exactly one real namespace was still flagged as a CS0104 collision —
        // and the advice to "use the fully-qualified name" could not resolve it.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Solo", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            bool flagged = result.Data!.Ambiguous?.Any(g => g.Name == "Solo") ?? false;
            Assert.IsFalse(flagged, "Solo.Ns.Solo exists in one real namespace; its ABI twin must not fabricate ambiguity.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_AbiProjectionType_StillResolvesByFullName()
    {
        // The filter is deliberately scoped to discovery. Exact lookups go through
        // LoadAllTypes, which stays unfiltered so an explicitly-named ABI type is reachable.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("ABI.Dup.Ns.Alpha", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, "Filtering search must not make ABI types unreachable by exact name.");
            Assert.AreEqual("ABI.Dup.Ns.Alpha", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_MicrosoftTypeVersusThirdPartyType_IsStillAmbiguous()
    {
        // The Microsoft-wins rule is only for the WinUI-3/UWP twin pair. A third-party
        // package shipping a same-named control must not be silently answered for by
        // the Microsoft type — that is the original bug, not the fix.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Widget", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
            StringAssert.Contains(result.Message, "Contoso.Controls.Widget");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_WinUiAndUwpTwin_ResolvesToTheMicrosoftType()
    {
        // Every duplicated short name in the SDK scope is essentially this pair, so
        // erroring here would break the documented 'members NavigationView' form. The
        // earlier tiebreak recognised only Microsoft.UI.Xaml, which silently handed a
        // Microsoft.UI.Input caller the UWP type instead.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Twin", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.AreEqual("Microsoft.UI.Input.Twin", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_AmbiguousShortName_IsInvalidInputListingCandidates()
    {
        // Silently picking one of several same-named types answers a question the caller
        // did not ask: the members of Dup.Ns.Alpha and Other.Ns.Alpha differ, and a wrong
        // pick is indistinguishable from a right one in the output.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Alpha", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
            StringAssert.Contains(result.Message, "Dup.Ns.Alpha");
            StringAssert.Contains(result.Message, "Other.Ns.Alpha");
            Assert.IsFalse(
                result.Message!.Contains("ABI.", StringComparison.Ordinal),
                "An ABI twin is the same type, not a competing candidate.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_ShortNameWithOnlyAnAbiTwin_StillResolves()
    {
        // Solo.Ns.Solo has an ABI twin but exists in exactly one real namespace, so the
        // ambiguity check must not turn a previously-working lookup into an error.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Solo", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.AreEqual("Solo.Ns.Solo", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void CheckProperty_AmbiguousShortName_IsInvalidInput()
    {
        // check-property drives whether a caller writes XAML against a property; a wrong
        // type resolution here yields a confident answer about the wrong type.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.CheckProperty("Alpha", "Anything", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
            StringAssert.Contains(result.Message, "ambiguous");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    private static string NewCacheDir() =>
        Path.Combine(Path.GetTempPath(), $"ApiQueryEngineDedupe_{Guid.NewGuid():N}");
    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Builds a cache where two packages each ship an identical set of namespaces and types —
    /// the real-world shape that produced duplicate search hits — plus ABI projection twins
    /// and a type whose only "second namespace" is its ABI twin.
    /// </summary>
    private static ProjectManifest BuildMultiPackageCache(string cacheDir)
    {
        var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
        {
            ["Dup.Ns"] =
            [
                SimpleType("Dup.Ns", "Alpha"),
                SimpleType("Dup.Ns", "AlphaOne"),
                SimpleType("Dup.Ns", "AlphaTwo"),
                SimpleType("Dup.Ns", "AlphaThree"),
                SimpleType("Dup.Ns", "AlphaFour"),
                SimpleType("Dup.Ns", "AlphaFive"),
            ],
            ["Other.Ns"] = [SimpleType("Other.Ns", "Alpha")],
            ["ABI.Dup.Ns"] = [SimpleType("ABI.Dup.Ns", "Alpha")],
            ["Solo.Ns"] = [SimpleType("Solo.Ns", "Solo")],
            ["ABI.Solo.Ns"] = [SimpleType("ABI.Solo.Ns", "Solo")],

            // The WinUI 3 / UWP twin shape: the same short name in a modern
            // Microsoft.* namespace and its legacy Windows.* counterpart.
            ["Microsoft.UI.Input"] = [SimpleType("Microsoft.UI.Input", "Twin")],
            ["Windows.UI.Input"] = [SimpleType("Windows.UI.Input", "Twin")],

            // A third-party type colliding with a Microsoft one is not a twin pair.
            ["Microsoft.UI.Xaml.Controls"] = [SimpleType("Microsoft.UI.Xaml.Controls", "Widget")],
            ["Contoso.Controls"] = [SimpleType("Contoso.Controls", "Widget")],
        };

        var packages = new List<ProjectPackageRef>();
        foreach (string packageId in DuplicatePackageIds)
        {
            WriteSyntheticPackage(cacheDir, packageId, namespaces);
            packages.Add(new ProjectPackageRef { Id = packageId, Version = "1.0.0" });
        }

        var manifest = new ProjectManifest
        {
            ProjectName = "DupApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "DupApp.csproj",
            Packages = packages,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(
            Path.Combine(projectsDir, "DupApp.json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        return manifest;
    }

    private static WinMdTypeInfo SimpleType(string ns, string name) => new()
    {
        Namespace = ns,
        Name = name,
        FullName = ns + "." + name,
        Kind = TypeKind.Class,
        SourceFile = "dup.winmd",
        Members = [],
    };

    private static void WriteSyntheticPackage(string cacheDir, string packageId, Dictionary<string, List<WinMdTypeInfo>> namespaces)
    {
        string packageDir = Path.Combine(cacheDir, "packages", packageId, "1.0.0");
        string typesDir = Path.Combine(packageDir, "types");
        Directory.CreateDirectory(typesDir);
        foreach ((string ns, List<WinMdTypeInfo> types) in namespaces)
        {
            File.WriteAllText(
                Path.Combine(typesDir, ns.Replace('.', '_') + ".json"),
                JsonSerializer.Serialize(types, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        }
        File.WriteAllText(
            Path.Combine(packageDir, "namespaces.json"),
            JsonSerializer.Serialize(namespaces.Keys.ToList(), ApiSearchJsonContext.Default.ListString));

        var meta = new PackageMeta
        {
            PackageId = packageId,
            Version = "1.0.0",
            WinMdFiles = ["dup.winmd"],
            TotalTypes = namespaces.Sum(kv => kv.Value.Count),
            TotalMembers = 0,
            TotalNamespaces = namespaces.Count,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(packageDir, "meta.json"), JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));
    }

    #endregion
}

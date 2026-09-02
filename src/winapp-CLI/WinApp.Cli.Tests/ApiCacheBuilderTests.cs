// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers how the cache builder maps a resolved package to the directory its metadata is
/// exported to. A package id and version do not identify what was cached — two projects
/// can resolve the same id and version to different files — so the mapping has to keep
/// them apart or one project silently answers from the other's metadata.
/// </summary>
[TestClass]
public sealed class ApiCacheBuilderTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ApiCacheBuilderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    [TestMethod]
    public void ResolvePackageExports_SameIdAndVersionFromDifferentFiles_GetSeparateCaches()
    {
        // Two projects reference "Contoso.Sdk 1.0.0" but resolve it to different .winmd
        // files — a rebuilt project reference, or two target frameworks selecting
        // different compile assets. Keyed on id and version alone, the second project
        // finds the first project's directory already claimed, records it as reused, and
        // its manifest then points at metadata built from the other project's files.
        string cacheDir = Path.Combine(_dir, "cache");
        PackageWithWinMd fromProjectA = WritePackage("a", "Contoso.Sdk", "1.0.0", "alpha");
        PackageWithWinMd fromProjectB = WritePackage("b", "Contoso.Sdk", "1.0.0", "beta-different-bytes");

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reused = 0;

        List<ProjectPackageRef> refsA = ApiCacheBuilder.ResolvePackageExports(
            [fromProjectA], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);
        List<ProjectPackageRef> refsB = ApiCacheBuilder.ResolvePackageExports(
            [fromProjectB], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);

        // Both projects must get their own export queued, and neither may be written off
        // as a reuse of the other.
        Assert.AreEqual(2, pendingExports.Count);
        Assert.AreEqual(0, reused);
        Assert.AreNotEqual(refsA.Single().SourceStamp, refsB.Single().SourceStamp);

        // And the manifest each project records has to resolve to its own directory.
        Assert.IsTrue(ApiCachePaths.TryPackageCacheDir(cacheDir, refsA.Single(), out string dirA));
        Assert.IsTrue(ApiCachePaths.TryPackageCacheDir(cacheDir, refsB.Single(), out string dirB));
        Assert.AreNotEqual(dirA, dirB);
    }

    [TestMethod]
    public void ResolvePackageExports_SamePackageFromSameFiles_IsExportedOnce()
    {
        // The reuse that does matter still has to happen: the Windows SDK and WinAppSDK
        // are shared by every project in a solution and must be parsed once per run.
        string cacheDir = Path.Combine(_dir, "cache");
        PackageWithWinMd shared = WritePackage("shared", "Contoso.Sdk", "1.0.0", "same");

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reused = 0;

        List<ProjectPackageRef> first = ApiCacheBuilder.ResolvePackageExports(
            [shared], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);
        List<ProjectPackageRef> second = ApiCacheBuilder.ResolvePackageExports(
            [shared], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);

        Assert.AreEqual(1, pendingExports.Count);
        Assert.AreEqual(1, reused);
        Assert.AreEqual(first.Single().SourceStamp, second.Single().SourceStamp);
    }

    /// <summary>
    /// Writes a .winmd whose bytes and path are unique to <paramref name="folder"/>, so
    /// the fingerprint the builder derives from it is distinguishable.
    /// </summary>
    private PackageWithWinMd WritePackage(string folder, string id, string version, string content)
    {
        string dir = Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        string winmd = Path.Combine(dir, id + ".winmd");
        File.WriteAllText(winmd, content);
        return new PackageWithWinMd(id, version, [winmd], []);
    }

    #region winapp.yaml project discovery

    private string NewProjectDir(string name)
    {
        string dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void DiscoverProjectFiles_FindsWinappYamlWhenThereIsNoMsBuildProject()
    {
        // An Electron app has no .csproj. Without this it discovers no project at all,
        // indexes nothing, and every query typed in it reports the API surface as absent.
        string dir = NewProjectDir("my-electron-app");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith("winapp.yaml", found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_PrefersTheMsBuildProjectOverWinappYaml()
    {
        // A .NET project may use winapp.yaml for its SDK packages. Its .csproj is the
        // more precise description of what it compiles against, and indexing both would
        // index the same directory twice under two names.
        string dir = NewProjectDir("dotnet-app");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project />");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith("App.csproj", found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_PrefersTheMsBuildProjectOverWinappYaml()
    {
        string dir = NewProjectDir("scanned");
        string app = Path.Combine(dir, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "winapp.yaml"), "packages: []");
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: true);

        Assert.HasCount(1, found);
        Assert.EndsWith("App.csproj", found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_SkipsWinappYamlUnderNodeModules()
    {
        string dir = NewProjectDir("with-deps");
        string nested = Path.Combine(dir, "node_modules", "some-package");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "winapp.yaml"), "packages: []");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: true);

        Assert.HasCount(1, found);
        Assert.AreEqual(Path.Combine(dir, "winapp.yaml"), found[0]);
    }

    [TestMethod]
    public void ProjectNameFor_WinappYaml_UsesTheDirectoryName()
    {
        // "winapp" is the file's own stem, so it would name every such project
        // identically. The directory is the app's name.
        string dir = NewProjectDir("my-electron-app");
        string projectFile = Path.Combine(dir, "winapp.yaml");

        Assert.AreEqual("my-electron-app", ApiCacheBuilder.ProjectNameFor(projectFile));
    }

    [TestMethod]
    public void ProjectNameFor_MsBuildProject_StillUsesTheFileName()
    {
        Assert.AreEqual("App", ApiCacheBuilder.ProjectNameFor(Path.Combine(_dir, "App.csproj")));
    }

    [TestMethod]
    public void FindProjectNameInDir_FindsAWinappYamlProject()
    {
        string dir = NewProjectDir("my-electron-app");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");

        Assert.AreEqual("my-electron-app", ApiCacheBuilder.FindProjectNameInDir(dir));
    }

    #endregion

    #region solution membership

    [TestMethod]
    public void DiscoverProjectFiles_SolutionDir_IndexesOnlyTheProjectsTheSolutionLists()
    {
        // A solution directory holds no project of its own. Recursing the tree there also
        // picks up sibling projects the solution deliberately excludes, and the caller is
        // then told to disambiguate against a project their solution never builds.
        string root = NewProjectDir("sln-listed");
        string listed = Path.Combine(root, "src", "App");
        string excluded = Path.Combine(root, "tools", "Unrelated");
        Directory.CreateDirectory(listed);
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(listed, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(excluded, "Unrelated.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"),
            """<Solution><Project Path="src/App/App.csproj" /></Solution>""");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_ClassicSln_IndexesOnlyTheProjectsTheSolutionLists()
    {
        string root = NewProjectDir("sln-classic");
        string listed = Path.Combine(root, "src", "App");
        string excluded = Path.Combine(root, "tools", "Unrelated");
        Directory.CreateDirectory(listed);
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(listed, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(excluded, "Unrelated.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.sln"),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_SolutionThatListsNothingReadable_FallsBackToScanning()
    {
        // An unparseable solution means membership is unknown, not that the solution
        // builds nothing. Indexing nothing there would report every API as absent.
        string root = NewProjectDir("sln-opaque");
        string app = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"), "not xml at all <<<");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_SolutionListingAMissingProject_FallsBackToScanning()
    {
        // Every listed path is gone (a stale solution). Treating that as "builds nothing"
        // would leave the projects that are actually present unindexed.
        string root = NewProjectDir("sln-stale");
        string app = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"),
            """<Solution><Project Path="src/Gone/Gone.csproj" /></Solution>""");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_ExplicitScan_StillWalksTheWholeTree()
    {
        // 'refresh --scan' is the caller explicitly asking for everything below a
        // directory, so solution membership must not narrow it.
        string root = NewProjectDir("sln-scan");
        string listed = Path.Combine(root, "src", "App");
        string excluded = Path.Combine(root, "tools", "Unrelated");
        Directory.CreateDirectory(listed);
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(listed, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(excluded, "Unrelated.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"),
            """<Solution><Project Path="src/App/App.csproj" /></Solution>""");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: true);

        Assert.HasCount(2, found);
    }

    #endregion
}

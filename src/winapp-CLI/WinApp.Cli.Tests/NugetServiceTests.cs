// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class NugetServiceTests : BaseCommandTests
{
    private INugetService _nugetService = null!;

    [TestInitialize]
    public void Setup()
    {
        _nugetService = GetRequiredService<INugetService>();
    }

    #region GetPackageDependenciesAsync Integration Tests

    [TestMethod]
    public async Task GetPackageDependenciesAsync_KnownPackageWithDependencies_ReturnsDependencies()
    {
        // Arrange - Newtonsoft.Json has no dependencies, but Microsoft.Extensions.Logging has dependencies
        var packageName = "Microsoft.Extensions.Logging";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result, "Should have at least one dependency");
        Assert.IsTrue(result.ContainsKey("Microsoft.Extensions.Logging.Abstractions"),
            "Should contain Microsoft.Extensions.Logging.Abstractions dependency");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_PackageWithMinimalDependencies_ReturnsDependencies()
    {
        // Arrange - Newtonsoft.Json has some framework-specific dependencies for older frameworks
        // This tests that the implementation returns all dependencies across all target framework groups
        var packageName = "Newtonsoft.Json";
        var version = "13.0.3";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        // Newtonsoft.Json has dependencies for older frameworks like net20, net35, etc.
        // The implementation returns all dependencies from all target framework groups
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_NonExistentPackage_ReturnsEmptyDictionary()
    {
        // Arrange
        var packageName = "This.Package.Does.Not.Exist.12345";
        var version = "1.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result, "Non-existent package should return empty dictionary");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_NonExistentVersion_ReturnsEmptyDictionary()
    {
        // Arrange
        var packageName = "Newtonsoft.Json";
        var version = "999.999.999"; // Non-existent version

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result, "Non-existent version should return empty dictionary");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_PackageWithVersionRanges_ReturnsVersionRanges()
    {
        // Arrange - Microsoft.Extensions.DependencyInjection uses version ranges
        var packageName = "Microsoft.Extensions.DependencyInjection";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result, "Should have dependencies");
        // Each dependency value is the resolved minimum version (normalized), never a raw range
        foreach (var dep in result)
        {
            Assert.IsFalse(string.IsNullOrEmpty(dep.Value), $"Dependency {dep.Key} should have a version");
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_CaseInsensitivePackageName_ReturnsDependencies()
    {
        // Arrange - use mixed case
        var packageName = "MICROSOFT.EXTENSIONS.LOGGING";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result, "Should have dependencies regardless of package name casing");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_ReturnsTransitiveDependencies()
    {
        // Arrange - Microsoft.Extensions.Logging 8.0.0 depends on
        // Microsoft.Extensions.DependencyInjection.Abstractions (direct dep),
        // and Microsoft.Extensions.Logging.Abstractions which itself depends on
        // Microsoft.Extensions.DependencyInjection.Abstractions (transitive).
        // We verify that a dependency of a dependency is included.
        var packageName = "Microsoft.Extensions.Logging";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.ContainsKey("Microsoft.Extensions.DependencyInjection.Abstractions"),
            "Should contain transitive dependency Microsoft.Extensions.DependencyInjection.Abstractions");
        Assert.IsTrue(result.ContainsKey("Microsoft.Extensions.Logging.Abstractions"),
            "Should contain direct dependency Microsoft.Extensions.Logging.Abstractions");
    }

    #endregion

    #region GetLatestVersionAsync Integration Tests

    [TestMethod]
    public async Task GetLatestVersionAsync_StableVersion_ReturnsNonEmptyVersion()
    {
        // Arrange - use a well-known package
        var packageName = "Newtonsoft.Json";

        // Act
        var result = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result), "Should return a non-empty version string");
        Assert.IsFalse(result.Contains('-', StringComparison.Ordinal), "Stable version should not contain prerelease suffix");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_ReturnedVersionIsListed()
    {
        // Arrange - use a well-known package and verify the returned version is actually listed on NuGet
        var packageName = "Newtonsoft.Json";

        // Act
        var version = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Assert - verify the version is listed by checking the registration API directly
        Assert.IsNotNull(version);
        var isListed = await IsVersionListedAsync(packageName, version, TestContext.CancellationToken);
        Assert.IsTrue(isListed, $"Returned version {version} should be listed on NuGet, but it appears to be unlisted");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_DoesNotReturnUnlistedVersions()
    {
        // Arrange - query all listed versions from the registration API and compare against GetLatestVersionAsync result
        var packageName = "Newtonsoft.Json";

        // Act
        var latestVersion = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Also get the flat container versions (which include unlisted) to verify filtering is happening
        var allVersions = await GetFlatContainerVersionsAsync(packageName, TestContext.CancellationToken);
        var listedVersions = await GetListedVersionsFromRegistrationAsync(packageName, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(latestVersion);
        Assert.Contains(latestVersion, listedVersions,
            $"GetLatestVersionAsync returned '{latestVersion}' which is not in the listed versions set");

        // If unlisted versions exist, verify the returned version isn't one of them
        var unlistedVersions = allVersions.Except(listedVersions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(latestVersion, unlistedVersions,
            $"GetLatestVersionAsync returned '{latestVersion}' which is an unlisted version");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WindowsAppSdk_StableVersion_ReturnsStableVersion()
    {
        // Arrange
        var packageName = "Microsoft.WindowsAppSDK";

        // Act
        var result = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(result.Contains('-', StringComparison.Ordinal), "Stable version should not contain prerelease suffix");
        var isListed = await IsVersionListedAsync(packageName, result, TestContext.CancellationToken);
        Assert.IsTrue(isListed, $"Returned version {result} should be listed on NuGet");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoneMode_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _nugetService.GetLatestVersionAsync("Newtonsoft.Json", SdkInstallMode.None, TestContext.CancellationToken));
    }

    /// <summary>
    /// Checks whether a specific version is listed on NuGet by querying the registration API.
    /// </summary>
    private static async Task<bool> IsVersionListedAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var url = $"https://api.nuget.org/v3/registration5-semver1/{packageName.ToLowerInvariant()}/index.json";
        using var resp = await http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("items", out var pages))
        {
            return false;
        }

        foreach (var page in pages.EnumerateArray())
        {
            JsonElement leafItems;
            if (page.TryGetProperty("items", out var inlineItems) && inlineItems.ValueKind == JsonValueKind.Array)
            {
                leafItems = inlineItems;
            }
            else
            {
                if (!page.TryGetProperty("@id", out var pageIdElem))
                {
                    continue;
                }

                var pageUrl = pageIdElem.GetString();
                if (string.IsNullOrEmpty(pageUrl))
                {
                    continue;
                }

                using var pageResp = await http.GetAsync(pageUrl, cancellationToken);
                pageResp.EnsureSuccessStatusCode();
                using var pageStream = await pageResp.Content.ReadAsStreamAsync(cancellationToken);
                using var pageDoc = await JsonDocument.ParseAsync(pageStream, cancellationToken: cancellationToken);

                if (!pageDoc.RootElement.TryGetProperty("items", out var fetchedItems))
                {
                    continue;
                }

                leafItems = fetchedItems;
            }

            foreach (var leaf in leafItems.EnumerateArray())
            {
                if (!leaf.TryGetProperty("catalogEntry", out var entry))
                {
                    continue;
                }

                if (entry.TryGetProperty("version", out var vProp) &&
                    string.Equals(vProp.GetString(), version, StringComparison.OrdinalIgnoreCase))
                {
                    // Default to listed=true if property is missing
                    if (entry.TryGetProperty("listed", out var listedProp))
                    {
                        return listedProp.GetBoolean();
                    }

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all versions (including unlisted) from the flat container API.
    /// </summary>
    private static async Task<HashSet<string>> GetFlatContainerVersionsAsync(string packageName, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageName.ToLowerInvariant()}/index.json";
        using var resp = await http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("versions", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                var v = el.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    versions.Add(v);
                }
            }
        }

        return versions;
    }

    /// <summary>
    /// Gets only listed versions from the registration API.
    /// </summary>
    private static async Task<HashSet<string>> GetListedVersionsFromRegistrationAsync(string packageName, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var url = $"https://api.nuget.org/v3/registration5-semver1/{packageName.ToLowerInvariant()}/index.json";
        using var resp = await http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("items", out var pages))
        {
            return versions;
        }

        foreach (var page in pages.EnumerateArray())
        {
            JsonElement leafItems;
            if (page.TryGetProperty("items", out var inlineItems) && inlineItems.ValueKind == JsonValueKind.Array)
            {
                leafItems = inlineItems;
            }
            else
            {
                if (!page.TryGetProperty("@id", out var pageIdElem))
                {
                    continue;
                }

                var pageUrl = pageIdElem.GetString();
                if (string.IsNullOrEmpty(pageUrl))
                {
                    continue;
                }

                using var pageResp = await http.GetAsync(pageUrl, cancellationToken);
                pageResp.EnsureSuccessStatusCode();
                using var pageStream = await pageResp.Content.ReadAsStreamAsync(cancellationToken);
                using var pageDoc = await JsonDocument.ParseAsync(pageStream, cancellationToken: cancellationToken);

                if (!pageDoc.RootElement.TryGetProperty("items", out var fetchedItems))
                {
                    continue;
                }

                leafItems = fetchedItems;
            }

            foreach (var leaf in leafItems.EnumerateArray())
            {
                if (!leaf.TryGetProperty("catalogEntry", out var entry))
                {
                    continue;
                }

                if (entry.TryGetProperty("listed", out var listedProp) && !listedProp.GetBoolean())
                {
                    continue;
                }

                if (entry.TryGetProperty("version", out var vProp))
                {
                    var v = vProp.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        versions.Add(v);
                    }
                }
            }
        }

        return versions;
    }

    #endregion

    #region CompareVersions Tests

    [TestMethod]
    public void CompareVersions_SimpleVersions_ComparesCorrectly()
    {
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0.0", "2.0.0"));
        Assert.IsGreaterThan(0, NugetService.CompareVersions("2.0.0", "1.0.0"));
        Assert.AreEqual(0, NugetService.CompareVersions("1.0.0", "1.0.0"));
    }

    [TestMethod]
    public void CompareVersions_DifferentLengths_ComparesCorrectly()
    {
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0", "1.0.1"));
        Assert.AreEqual(0, NugetService.CompareVersions("1.0.0.0", "1.0"));
    }

    [TestMethod]
    public void CompareVersions_WithPrereleaseTags_ComparesCorrectly()
    {
        // Uses NuGet SemVer 2.0 ordering: numbered prerelease tags order by their number, and a stable
        // release outranks its own prerelease. (The previous numeric-only split treated all of these as
        // equal, which made "latest" selection for preview/experimental channels non-deterministic.)

        // 1.0.0-preview1 < 1.0.0-preview2
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0.0-preview1", "1.0.0-preview2"));
        Assert.IsGreaterThan(0, NugetService.CompareVersions("1.0.0-preview2", "1.0.0-preview1"));

        // A stable release is greater than its prerelease of the same version.
        Assert.IsGreaterThan(0, NugetService.CompareVersions("1.0.0", "1.0.0-preview"));
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0.0-preview", "1.0.0"));
    }

    #endregion

    #region ParseMinimumVersion

    [TestMethod]
    // Plain versions
    [DataRow("1.0.0", "1.0.0")]
    [DataRow("  1.2.3  ", "1.2.3")]
    [DataRow("1.0.0-preview", "1.0.0-preview")]
    // Bracketed exact / open ranges
    [DataRow("[1.0.0]", "1.0.0")]
    [DataRow("[1.0.0, )", "1.0.0")]
    [DataRow("[1.0.0,)", "1.0.0")]
    [DataRow("(1.0.0, 2.0.0)", "1.0.0")]
    [DataRow("[2.0.300, 3.0.0)", "2.0.300")]
    // Bracket-stripped form (caller pre-cleaned brackets but left the comma).
    // Regression guard for the bug where ParseMinimumVersion would short-circuit
    // on "no brackets present" and return the comma-joined string verbatim,
    // producing 404s when used as a download version.
    [DataRow("2.0.300, 3.0.0", "2.0.300")]
    [DataRow("1.0.0,2.0.0", "1.0.0")]
    // Empty / whitespace
    [DataRow("", "")]
    [DataRow("   ", "")]
    public void ParseMinimumVersion_ReturnsExpectedLowerBound(string input, string expected)
    {
        var actual = NugetService.ParseMinimumVersion(input);
        Assert.AreEqual(expected, actual, $"ParseMinimumVersion(\"{input}\")");
    }

    #endregion

    #region Private nuget.config (isolated feed) Tests

    // These tests exercise the NuGet.Client-backed behavior directly against a temporary
    // nuget.config with local folder sources (no network). They cover the private-feed scenarios the
    // migration enables: <packageSourceMapping> source selection and globalPackagesFolder resolution.
    // The temp root lives under %TEMP% (outside the repo) so the repo's own nuget.config is not in
    // its configuration hierarchy; <clear /> removes any inherited machine/user sources.

    private static readonly string[] AlphaAndBeta = ["alpha", "beta"];
    private static readonly string[] AlphaOnly = ["alpha"];
    private static readonly string[] BetaOnly = ["beta"];

    /// <summary>
    /// <see cref="IWinappDirectoryService"/> whose global directory is the real default
    /// (<c>%USERPROFILE%\.winapp</c>), so <see cref="NugetService"/> does NOT treat it as a test
    /// override and instead resolves the global packages folder from the supplied nuget.config
    /// (exercising <c>SettingsUtility.GetGlobalPackagesFolder</c>).
    /// </summary>
    private sealed class DefaultWinappDirectoryService : IWinappDirectoryService
    {
        public DirectoryInfo GetGlobalWinappDirectory() =>
            new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp"));

        public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null) =>
            new(Path.Combine((baseDirectory ?? new DirectoryInfo(Directory.GetCurrentDirectory())).FullName, ".winapp"));

        public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
        {
        }
    }

    private static NugetService CreateServiceRootedAt(DirectoryInfo root) =>
        new(new DefaultWinappDirectoryService(), new CurrentDirectoryProvider(root.FullName));

    private static DirectoryInfo CreateFeedTestDirectory()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"NugetServiceFeedTests_{Guid.NewGuid():N}"));
        dir.Create();
        return dir;
    }

    private static void WriteNuGetConfig(DirectoryInfo dir, string contents) =>
        File.WriteAllText(Path.Combine(dir.FullName, "nuget.config"), contents);

    private static void TryDelete(DirectoryInfo dir)
    {
        try
        {
            dir.Refresh();
            if (dir.Exists)
            {
                dir.Delete(true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [TestMethod]
    public void GetRepositoriesForPackage_NoPackageSourceMapping_ReturnsAllConfiguredSources()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                    <add key="beta" value="beta-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var sources = service.GetRepositoriesForPackage("Any.Package")
                .Select(r => r.PackageSource.Name)
                .ToList();

            CollectionAssert.AreEquivalent(
                AlphaAndBeta,
                sources,
                "With no packageSourceMapping in effect, every configured source is eligible.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetRepositoriesForPackage_PackageSourceMapping_SelectsOnlyMappedSource()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                    <add key="beta" value="beta-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="alpha">
                      <package pattern="Contoso.*" />
                    </packageSource>
                    <packageSource key="beta">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            // Contoso.* is mapped exclusively to 'alpha'.
            var mapped = service.GetRepositoriesForPackage("Contoso.Widget")
                .Select(r => r.PackageSource.Name)
                .ToList();
            CollectionAssert.AreEqual(AlphaOnly, mapped, "Contoso.* must resolve to the mapped source only.");

            // Everything else falls back to the '*' mapping on 'beta'.
            var fallback = service.GetRepositoriesForPackage("Fabrikam.Thing")
                .Select(r => r.PackageSource.Name)
                .ToList();
            CollectionAssert.AreEqual(BetaOnly, fallback, "Unmatched packages use the '*' mapping.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetRepositoriesForPackage_PackageSourceMapping_UnmappedPackage_ReturnsEmpty()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // Mapping is enabled but only maps Contoso.* -> alpha; there is no '*' fallback, so a
            // package matching no pattern must resolve to zero sources rather than silently falling
            // back to an unmapped feed (the dependency-confusion behavior the reviewer flagged).
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                    <add key="beta" value="beta-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="alpha">
                      <package pattern="Contoso.*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var sources = service.GetRepositoriesForPackage("Unmapped.Package").ToList();

            Assert.IsEmpty(sources, "A package matching no packageSourceMapping pattern must resolve to no sources.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_HonorsGlobalPackagesFolderFromConfig()
    {
        // GetGlobalPackagesFolder gives NUGET_PACKAGES precedence over the config value; skip if the
        // host has it set so the assertion stays meaningful.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="custom-packages" />
                  </config>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var expected = new DirectoryInfo(Path.Combine(root.FullName, "custom-packages")).FullName;
            var actual = service.GetNuGetGlobalPackagesDir().FullName;

            Assert.AreEqual(
                expected.TrimEnd(Path.DirectorySeparatorChar),
                actual.TrimEnd(Path.DirectorySeparatorChar),
                "globalPackagesFolder from nuget.config should be honored (resolved relative to the config).");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetNuGetPackageDir_NormalizesIdAndVersionToOnDiskLayout()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="custom-packages" />
                  </config>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            // "1.0" is a valid NuGet version that NuGet stores under its normalized "1.0.0" folder, and
            // the package-id folder is lowercased. GetNuGetPackageDir must match that on-disk layout so
            // callers find the extracted package regardless of how the version string was expressed.
            var dir = service.GetNuGetPackageDir("Some.Package", "1.0");

            Assert.AreEqual("1.0.0", dir.Name, "Version should be normalized to the on-disk folder name.");
            Assert.AreEqual("some.package", dir.Parent?.Name, "Package id folder should be lowercased.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    // Shorthand numeric versions expand to NuGet's canonical 3-part form so the stored/returned value
    // matches the on-disk global-packages folder layout that downstream cache-path builders concatenate.
    [DataRow("1.0", "1.0.0")]
    [DataRow("2", "2.0.0")]
    [DataRow("1.2.3", "1.2.3")]
    // Trailing-zero revision is dropped by normalization (1.2.3.0 -> 1.2.3), matching NuGet on disk.
    [DataRow("1.2.3.0", "1.2.3")]
    // Prerelease and metadata are preserved (build metadata is stripped by NuGet normalization).
    [DataRow("1.0.0-preview.1", "1.0.0-preview.1")]
    [DataRow("1.0.0+build5", "1.0.0")]
    // Non-parseable input is returned unchanged rather than throwing.
    [DataRow("not-a-version", "not-a-version")]
    public void NormalizeVersion_ReturnsCanonicalOnDiskForm(string input, string expected)
    {
        Assert.AreEqual(expected, NugetService.NormalizeVersion(input), $"NormalizeVersion(\"{input}\")");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // One eligible source so version enumeration actually enters the per-source loop, where the
            // cancellation token is observed — rather than short-circuiting to "no versions found".
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // Cancellation must surface as OperationCanceledException, not be masked as a "no versions" error.
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                async () => await service.GetLatestVersionAsync("Any.Package", SdkInstallMode.Stable, cts.Token));
        }
        finally
        {
            TryDelete(root);
        }
    }

    #endregion
}

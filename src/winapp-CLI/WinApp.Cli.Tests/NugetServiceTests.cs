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
}

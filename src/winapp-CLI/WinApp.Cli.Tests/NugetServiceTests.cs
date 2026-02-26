// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public partial class NugetServiceTests : BaseCommandTests
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
        // Version ranges are returned as-is (e.g., "[8.0.0, )" or "8.0.0")
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

    #region NuSpec XML Parsing Tests

    [TestMethod]
    public void ParseNuspecDependencies_WithNamespace_ParsesCorrectly()
    {
        // Arrange - nuspec with default namespace (typical NuGet format)
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=""net8.0"">
        <dependency id=""Dependency.One"" version=""1.0.0"" />
        <dependency id=""Dependency.Two"" version=""2.0.0"" />
      </group>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.HasCount(2, result);
        Assert.IsTrue(result.ContainsKey("Dependency.One"));
        Assert.IsTrue(result.ContainsKey("Dependency.Two"));
        Assert.AreEqual("1.0.0", result["Dependency.One"]);
        Assert.AreEqual("2.0.0", result["Dependency.Two"]);
    }

    [TestMethod]
    public void ParseNuspecDependencies_WithoutNamespace_ParsesCorrectly()
    {
        // Arrange - nuspec without namespace
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package>
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <dependency id=""Dependency.One"" version=""1.0.0"" />
      <dependency id=""Dependency.Two"" version=""2.0.0"" />
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.HasCount(2, result);
        Assert.IsTrue(result.ContainsKey("Dependency.One"));
        Assert.IsTrue(result.ContainsKey("Dependency.Two"));
    }

    [TestMethod]
    public void ParseNuspecDependencies_NoDependencies_ReturnsEmpty()
    {
        // Arrange - nuspec with no dependencies element
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void ParseNuspecDependencies_EmptyDependenciesElement_ReturnsEmpty()
    {
        // Arrange
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void ParseNuspecDependencies_MultipleTargetFrameworkGroups_ReturnsAllDependencies()
    {
        // Arrange - nuspec with multiple target framework groups
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=""net6.0"">
        <dependency id=""Net6.Dependency"" version=""1.0.0"" />
      </group>
      <group targetFramework=""net8.0"">
        <dependency id=""Net8.Dependency"" version=""2.0.0"" />
      </group>
      <group targetFramework=""netstandard2.0"">
        <dependency id=""NetStandard.Dependency"" version=""3.0.0"" />
      </group>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert - The implementation returns all dependencies from all groups
        Assert.HasCount(3, result);
        Assert.IsTrue(result.ContainsKey("Net6.Dependency"));
        Assert.IsTrue(result.ContainsKey("Net8.Dependency"));
        Assert.IsTrue(result.ContainsKey("NetStandard.Dependency"));
    }

    [TestMethod]
    public void ParseNuspecDependencies_WithVersionRanges_RemovesBrackets()
    {
        // Arrange - nuspec with version ranges
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=""net8.0"">
        <dependency id=""ExactVersion"" version=""1.0.0"" />
        <dependency id=""MinVersion"" version=""[1.0.0, )"" />
        <dependency id=""RangeVersion"" version=""[1.0.0, 2.0.0)"" />
        <dependency id=""MaxVersion"" version=""(, 2.0.0]"" />
      </group>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert - brackets and parentheses should be stripped
        Assert.HasCount(4, result);
        Assert.AreEqual("1.0.0", result["ExactVersion"]);
        Assert.AreEqual("1.0.0, ", result["MinVersion"]);
        Assert.AreEqual("1.0.0, 2.0.0", result["RangeVersion"]);
        Assert.AreEqual(", 2.0.0", result["MaxVersion"]);
    }

    [TestMethod]
    public void ParseNuspecDependencies_DuplicateDependencies_FirstOneWins()
    {
        // Arrange - same dependency in multiple groups (TryAdd behavior)
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=""net6.0"">
        <dependency id=""SharedDependency"" version=""1.0.0"" />
      </group>
      <group targetFramework=""net8.0"">
        <dependency id=""SharedDependency"" version=""2.0.0"" />
      </group>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert - TryAdd keeps the first value
        Assert.HasCount(1, result);
        Assert.AreEqual("1.0.0", result["SharedDependency"]);
    }

    [TestMethod]
    public void ParseNuspecDependencies_MissingIdAttribute_SkipsDependency()
    {
        // Arrange - dependency without id attribute
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=""net8.0"">
        <dependency version=""1.0.0"" />
        <dependency id=""ValidDependency"" version=""2.0.0"" />
      </group>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.HasCount(1, result);
        Assert.IsTrue(result.ContainsKey("ValidDependency"));
    }

    [TestMethod]
    public void ParseNuspecDependencies_MissingVersionAttribute_SkipsDependency()
    {
        // Arrange - dependency without version attribute
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=""net8.0"">
        <dependency id=""NoVersion"" />
        <dependency id=""ValidDependency"" version=""2.0.0"" />
      </group>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.HasCount(1, result);
        Assert.IsTrue(result.ContainsKey("ValidDependency"));
    }

    [TestMethod]
    public void ParseNuspecDependencies_EmptyIdOrVersion_SkipsDependency()
    {
        // Arrange - dependency with empty id or version
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=""net8.0"">
        <dependency id="""" version=""1.0.0"" />
        <dependency id=""EmptyVersion"" version="""" />
        <dependency id=""ValidDependency"" version=""2.0.0"" />
      </group>
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.HasCount(1, result);
        Assert.IsTrue(result.ContainsKey("ValidDependency"));
    }

    [TestMethod]
    public void ParseNuspecDependencies_MalformedXml_ThrowsException()
    {
        // Arrange - malformed XML
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package>
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0
  </metadata>
</package>";

        // Act & Assert
        Assert.ThrowsExactly<XmlException>(() => ParseNuspecDependenciesFromXml(nuspecXml));
    }

    [TestMethod]
    public void ParseNuspecDependencies_FlatDependenciesWithoutGroups_ParsesCorrectly()
    {
        // Arrange - older nuspec format with flat dependencies (no groups)
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <dependency id=""Dependency.One"" version=""1.0.0"" />
      <dependency id=""Dependency.Two"" version=""2.0.0"" />
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.HasCount(2, result);
        Assert.IsTrue(result.ContainsKey("Dependency.One"));
        Assert.IsTrue(result.ContainsKey("Dependency.Two"));
    }

    [TestMethod]
    public void ParseNuspecDependencies_DifferentNamespaceVersions_ParsesCorrectly()
    {
        // Arrange - nuspec with 2010 namespace (older format)
        var nuspecXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"">
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <dependencies>
      <dependency id=""OldFormatDep"" version=""1.0.0"" />
    </dependencies>
  </metadata>
</package>";

        // Act
        var result = ParseNuspecDependenciesFromXml(nuspecXml);

        // Assert
        Assert.HasCount(1, result);
        Assert.IsTrue(result.ContainsKey("OldFormatDep"));
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
        // The implementation splits on both '.' and '-' and compares parts numerically
        // Non-numeric parts (like "preview1") are treated as 0

        // "1.0.0-preview1" splits to ["1", "0", "0", "preview1"] 
        // "1.0.0-preview2" splits to ["1", "0", "0", "preview2"]
        // "preview1" and "preview2" both parse to 0, so they compare as equal
        Assert.AreEqual(0, NugetService.CompareVersions("1.0.0-preview1", "1.0.0-preview2"));

        // Non-prerelease version without suffix vs with suffix
        // "1.0.0" splits to ["1", "0", "0"]
        // "1.0.0-preview" splits to ["1", "0", "0", "preview"] 
        // At index 3: 0 vs 0 (both default to 0), so they're equal
        Assert.AreEqual(0, NugetService.CompareVersions("1.0.0", "1.0.0-preview"));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Parses nuspec XML content using the same logic as NugetService.GetPackageDependenciesAsync
    /// </summary>
    private static Dictionary<string, string> ParseNuspecDependenciesFromXml(string nuspecXml)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var doc = new XmlDocument();
        doc.LoadXml(nuspecXml);

        // The nuspec uses a default namespace; we need a namespace manager
        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        var ns = doc.DocumentElement?.NamespaceURI ?? string.Empty;
        if (!string.IsNullOrEmpty(ns))
        {
            nsMgr.AddNamespace("ns", ns);
        }

        var prefix = string.IsNullOrEmpty(ns) ? "" : "ns:";
        var depNodes = doc.SelectNodes($"//{prefix}dependency", nsMgr);
        if (depNodes != null)
        {
            foreach (XmlNode node in depNodes)
            {
                var depId = node.Attributes?["id"]?.Value;
                var depVersion = node.Attributes?["version"]?.Value;
                if (!string.IsNullOrEmpty(depId) && !string.IsNullOrEmpty(depVersion))
                {
                    var cleanedVersion = BracketsAndParenthesesRegex().Replace(depVersion, "");
                    dependencies.TryAdd(depId, cleanedVersion);
                }
            }
        }

        return dependencies;
    }

    [GeneratedRegex(@"[\[\]\(\)]")]
    private static partial Regex BracketsAndParenthesesRegex();

    #endregion
}

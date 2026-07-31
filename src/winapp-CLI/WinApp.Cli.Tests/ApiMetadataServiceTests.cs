// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <see cref="ApiMetadataService"/> project-manifest resolution and the
/// no-project error paths, without requiring a real package cache. Resolution
/// success is asserted indirectly: once a manifest resolves, the query reaches
/// the engine and returns a non-<see cref="ApiQueryOutcome.NoProject"/> outcome.
/// </summary>
[TestClass]
public sealed class ApiMetadataServiceTests
{
    private string _globalDir = null!;
    private string _currentDir = null!;
    private string _projectsDir = null!;

    [TestInitialize]
    public void Setup()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ApiMetadataServiceTests_{Guid.NewGuid():N}");
        _globalDir = Path.Combine(root, "global");
        _currentDir = Path.Combine(root, "current");
        Directory.CreateDirectory(_globalDir);
        Directory.CreateDirectory(_currentDir);
        _projectsDir = Path.Combine(_globalDir, "cache", "find-api", "projects");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Directory.GetParent(_globalDir)!.FullName, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private ApiMetadataService CreateService() => new(
        new FakeWinappDirectoryService(new DirectoryInfo(_globalDir)),
        new CurrentDirectoryProvider(_currentDir),
        NullLogger<ApiMetadataService>.Instance);

    private void WriteManifest(string name, string? projectDir = null)
    {
        Directory.CreateDirectory(_projectsDir);
        var manifest = new ProjectManifest
        {
            ProjectName = name,
            ProjectDir = projectDir ?? Path.Combine(_currentDir, name),
            ProjectFile = name + ".csproj",
            Packages = [new ProjectPackageRef { Id = "Some.Pkg", Version = "1.0.0" }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(_projectsDir, name + ".json"), JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
    }

    [TestMethod]
    public void Query_NoIndex_ReturnsNoProject()
    {
        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "find-api refresh");
    }

    [TestMethod]
    public void Query_SingleIndexedProject_ResolvesAndReachesEngine()
    {
        WriteManifest("Alpha");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, null));

        // Manifest resolved (lone project), so the engine ran and reported the missing
        // type rather than a NoProject resolution failure.
        Assert.AreEqual(ApiQueryOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public void Query_MultipleProjects_NoScope_IsAmbiguous()
    {
        WriteManifest("Alpha");
        WriteManifest("Beta");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "Multiple projects");
    }

    [TestMethod]
    public void Query_ProjectOption_SelectsNamedProject()
    {
        WriteManifest("Alpha");
        WriteManifest("Beta");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, "Beta"));

        // Named project resolved -> engine ran (type missing), not a NoProject failure.
        Assert.AreEqual(ApiQueryOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public void Query_UnknownProjectOption_ReportsNotIndexed()
    {
        WriteManifest("Alpha");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, "DoesNotExist"));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "not indexed");
    }

    [TestMethod]
    public void Query_ExplicitProjectDir_NoMatch_DoesNotFallBackToLoneProject()
    {
        // Regression (C2): an explicit --project-dir that matches no indexed
        // project must report "not indexed" rather than silently answering for the
        // unrelated lone cached project.
        WriteManifest("Alpha");
        string unrelated = Path.Combine(Directory.GetParent(_globalDir)!.FullName, "unrelated");
        Directory.CreateDirectory(unrelated);

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(unrelated, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "No indexed API metadata was found for");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProjectDetectionServiceTests
{
    private string _tempDir = null!;
    private ProjectDetectionService _sut = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ProjectDetectTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private DirectoryInfo Root => new(_tempDir);

    private string CreateDir(params string[] segments)
    {
        var path = Path.Combine([_tempDir, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void CreateFile(string relativePath, string content = "")
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // --- Detection rule tests ---

    [TestMethod]
    public void DetectProject_Dotnet_Csproj()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_WinExe()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_ExcludesLibrary()
    {
        CreateFile("MyLib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Dotnet_ExcludesDefaultOutputType()
    {
        // No OutputType defaults to Library
        CreateFile("MyLib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Dotnet_ExcludesTestProject()
    {
        CreateFile("MyApp.Tests.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Dotnet_IncludesExeWithIsTestProjectFalse()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <IsTestProject>false</IsTestProject>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_PicksExeOverLibraryInSameDir()
    {
        // Directory has both a library and an exe; should still detect
        CreateFile("MyLib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
          </PropertyGroup>
        </Project>
        """);
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Rust_CargoToml()
    {
        CreateFile("Cargo.toml", "[package]");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Rust, result.Type);
    }

    [TestMethod]
    public void DetectProject_CPP_CMakeLists()
    {
        CreateFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.0)");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.CPP, result.Type);
    }

    [TestMethod]
    public void DetectProject_Flutter_Pubspec()
    {
        CreateFile("pubspec.yaml", "name: my_app");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Flutter, result.Type);
    }

    [TestMethod]
    public void DetectProject_Electron_PackageJson()
    {
        CreateFile("package.json", """
        {
            "name": "my-electron-app",
            "devDependencies": {
                "electron": "^28.0.0"
            }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Electron, result.Type);
    }

    [TestMethod]
    public void DetectProject_Electron_InDependencies()
    {
        CreateFile("package.json", """
        {
            "name": "my-app",
            "dependencies": {
                "electron": "^28.0.0"
            }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Electron, result.Type);
    }

    [TestMethod]
    public void DetectProject_NotElectron_NoElectronDep()
    {
        CreateFile("package.json", """
        {
            "name": "my-app",
            "dependencies": {
                "react": "^18.0.0"
            }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Tauri_ConfInSubdir()
    {
        CreateDir("src-tauri");
        CreateFile(Path.Combine("src-tauri", "tauri.conf.json"), "{}");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Tauri, result.Type);
    }

    [TestMethod]
    public void DetectProject_NoProject_EmptyDir()
    {
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    // --- Specificity tests ---

    [TestMethod]
    public void DetectProject_TauriOverRust_WhenBothPresent()
    {
        // Tauri projects typically also have Cargo.toml
        CreateFile("Cargo.toml", "[package]");
        CreateDir("src-tauri");
        CreateFile(Path.Combine("src-tauri", "tauri.conf.json"), "{}");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Tauri, result.Type);
    }

    [TestMethod]
    public void DetectProject_ElectronOverNothing_WhenPackageJsonHasElectron()
    {
        CreateFile("package.json", """
        {
            "devDependencies": { "electron": "^28.0.0" }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Electron, result.Type);
    }

    [TestMethod]
    public void DetectProject_DotnetOverCpp_WhenBothPresent()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        CreateFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.0)");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_FlutterOverRust_WhenBothPresent()
    {
        CreateFile("pubspec.yaml", "name: my_app");
        CreateFile("Cargo.toml", "[package]");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Flutter, result.Type);
    }

    // --- BFS tests ---

    [TestMethod]
    public async Task DetectProjects_BFS_FindsRootFirst()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        CreateDir("sub");
        CreateFile(Path.Combine("sub", "Cargo.toml"), "[package]");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        // Root project should be found; sub should be pruned
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(DetectedProjectType.Dotnet, results[0].Type);
        Assert.AreEqual(".", results[0].DisplayPath);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_FindsMultipleAtSameLevel()
    {
        CreateDir("app1");
        CreateFile(Path.Combine("app1", "Cargo.toml"), "[package]");
        CreateDir("app2");
        CreateFile(Path.Combine("app2", "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.0)");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(2, results.Count);
        var types = results.Select(r => r.Type).ToHashSet();
        Assert.IsTrue(types.Contains(DetectedProjectType.Rust));
        Assert.IsTrue(types.Contains(DetectedProjectType.CPP));
    }

    [TestMethod]
    public async Task DetectProjects_BFS_PrunesSubdirectories()
    {
        // Create a project at sub/app, and a nested one at sub/app/nested
        CreateDir("sub", "app");
        CreateFile(Path.Combine("sub", "app", "Cargo.toml"), "[package]");
        CreateDir("sub", "app", "nested");
        CreateFile(Path.Combine("sub", "app", "nested", "CMakeLists.txt"), "cmake");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        // Only the parent project should be found, nested should be pruned
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(DetectedProjectType.Rust, results[0].Type);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_RespectsMaxProjects()
    {
        for (int i = 0; i < 10; i++)
        {
            CreateDir($"app{i}");
            CreateFile(Path.Combine($"app{i}", "Cargo.toml"), "[package]");
        }

        var results = await _sut.DetectProjectsAsync(Root, 3, null, CancellationToken.None);

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_SkipsIgnoredDirectories()
    {
        CreateDir("node_modules", "some-pkg");
        CreateFile(Path.Combine("node_modules", "some-pkg", "Cargo.toml"), "[package]");
        CreateDir("src");
        CreateFile(Path.Combine("src", "CMakeLists.txt"), "cmake");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(DetectedProjectType.CPP, results[0].Type);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_ReportsProgress()
    {
        CreateDir("app1");
        CreateFile(Path.Combine("app1", "Cargo.toml"), "[package]");
        CreateDir("app2");
        CreateFile(Path.Combine("app2", "CMakeLists.txt"), "cmake");

        var reported = new List<DetectedProject>();
        var progress = new SynchronousProgress<DetectedProject>(p => reported.Add(p));

        var results = await _sut.DetectProjectsAsync(Root, 5, progress, CancellationToken.None);

        Assert.AreEqual(results.Count, reported.Count);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_EmptyDir_ReturnsEmpty()
    {
        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);
        Assert.AreEqual(0, results.Count);
    }

    // --- Display path tests ---

    [TestMethod]
    public void DetectProject_DisplayPath_RootIsDot()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(".", result.DisplayPath);
    }

    [TestMethod]
    public async Task DetectProject_DisplayPath_NestedUsesForwardSlash()
    {
        CreateDir("src", "my-app");
        CreateFile(Path.Combine("src", "my-app", "Cargo.toml"), "[package]");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("src/my-app", results[0].DisplayPath);
    }

    // --- DetectedProject model tests ---

    [TestMethod]
    public void DetectedProject_ToDisplayString()
    {
        var project = new DetectedProject(DetectedProjectType.Tauri, Root, "src/my-app");
        Assert.AreEqual("Tauri project at src/my-app", project.ToDisplayString());
    }

    [TestMethod]
    public void DetectedProject_TypeLabel_AllTypes()
    {
        Assert.AreEqual("Tauri", new DetectedProject(DetectedProjectType.Tauri, Root, ".").TypeLabel);
        Assert.AreEqual("Electron", new DetectedProject(DetectedProjectType.Electron, Root, ".").TypeLabel);
        Assert.AreEqual("Flutter", new DetectedProject(DetectedProjectType.Flutter, Root, ".").TypeLabel);
        Assert.AreEqual(".NET", new DetectedProject(DetectedProjectType.Dotnet, Root, ".").TypeLabel);
        Assert.AreEqual("Rust", new DetectedProject(DetectedProjectType.Rust, Root, ".").TypeLabel);
        Assert.AreEqual("C++", new DetectedProject(DetectedProjectType.CPP, Root, ".").TypeLabel);
    }

    // --- Edge cases ---

    [TestMethod]
    public void DetectProject_InvalidPackageJson_NotElectron()
    {
        CreateFile("package.json", "not valid json {{{");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_EmptyPackageJson_NotElectron()
    {
        CreateFile("package.json", "{}");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DetectProjects_Cancellation_Throws()
    {
        CreateDir("app1");
        CreateFile(Path.Combine("app1", "Cargo.toml"), "[package]");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await _sut.DetectProjectsAsync(Root, 5, null, cts.Token));
    }
}

/// <summary>
/// A synchronous IProgress implementation that invokes the callback immediately
/// on the calling thread, avoiding the async post behavior of Progress&lt;T&gt;.
/// </summary>
file sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

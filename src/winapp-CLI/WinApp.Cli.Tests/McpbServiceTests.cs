// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class McpbServiceTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"McpbTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    #region Version Conversion Tests

    [TestMethod]
    [DataRow("1.0.0", "1.0.0.0")]
    [DataRow("2.1", "2.1.0.0")]
    [DataRow("3", "3.0.0.0")]
    [DataRow("1.2.3.4", "1.2.3.4")]
    [DataRow("1.0.0.0", "1.0.0.0")]
    [DataRow("10.20.30", "10.20.30.0")]
    public void ConvertToMsixVersion_ConvertsCorrectly(string input, string expected)
    {
        var result = McpbService.ConvertToMsixVersion(input);
        Assert.AreEqual(expected, result);
    }

    #endregion

    #region Package Name Sanitization Tests

    [TestMethod]
    [DataRow("SampleMcpServer", "SampleMcpServer")]
    [DataRow("my-mcp-server", "my.mcp.server")]
    [DataRow("my_mcp_server", "my.mcp.server")]
    [DataRow("server@v2", "server.v2")]
    [DataRow("com.example.server", "com.example.server")]
    [DataRow("Server With Spaces", "Server.With.Spaces")]
    public void SanitizePackageName_SanitizesCorrectly(string input, string expected)
    {
        var result = McpbService.SanitizePackageName(input);
        Assert.AreEqual(expected, result);
    }

    #endregion

    #region Server ID Sanitization Tests

    [TestMethod]
    [DataRow("SampleMcpServer", "SampleMcpServer")]
    [DataRow("my-mcp-server", "mymcpserver")]
    [DataRow("my_mcp_server", "mymcpserver")]
    [DataRow("com.example.server", "comexampleserver")]
    public void SanitizeServerId_SanitizesCorrectly(string input, string expected)
    {
        var result = McpbService.SanitizeServerId(input);
        Assert.AreEqual(expected, result);
    }

    #endregion

    #region Manifest Parsing Tests

    [TestMethod]
    public void McpbManifest_Deserializes_ValidManifest()
    {
        var json = """
        {
            "manifest_version": "0.1",
            "name": "TestServer",
            "version": "1.0.0",
            "description": "A test MCP server",
            "author": { "name": "TestAuthor" },
            "server": {
                "type": "binary",
                "entry_point": "TestServer.exe"
            },
            "_meta": {
                "com.microsoft.windows": {
                    "static_responses": {
                        "initialize": { "protocolVersion": "2025-06-18" },
                        "tools/list": { "tools": [] }
                    }
                }
            }
        }
        """;

        var manifest = JsonSerializer.Deserialize(json, McpbJsonContext.Default.McpbManifest);

        Assert.IsNotNull(manifest);
        Assert.AreEqual("TestServer", manifest.Name);
        Assert.AreEqual("1.0.0", manifest.Version);
        Assert.AreEqual("A test MCP server", manifest.Description);
        Assert.AreEqual("TestAuthor", manifest.Author?.Name);
        Assert.AreEqual("binary", manifest.Server?.Type);
        Assert.AreEqual("TestServer.exe", manifest.Server?.EntryPoint);
    }

    [TestMethod]
    public void McpbManifest_GetWindowsMeta_ReturnsStaticResponses()
    {
        var json = """
        {
            "name": "TestServer",
            "version": "1.0.0",
            "description": "Test",
            "server": { "type": "binary", "entry_point": "test.exe" },
            "_meta": {
                "com.microsoft.windows": {
                    "static_responses": {
                        "initialize": { "protocolVersion": "2025-06-18" },
                        "tools/list": { "tools": [] }
                    },
                    "capabilities": ["documentsLibrary", "downloadsFolder"]
                }
            }
        }
        """;

        var manifest = JsonSerializer.Deserialize(json, McpbJsonContext.Default.McpbManifest);
        Assert.IsNotNull(manifest);

        var windowsMeta = manifest.GetWindowsMeta();
        Assert.IsNotNull(windowsMeta);
        Assert.IsNotNull(windowsMeta.StaticResponses);
        Assert.IsNotNull(windowsMeta.StaticResponses.Initialize);
        Assert.IsNotNull(windowsMeta.StaticResponses.ToolsList);
        Assert.IsNotNull(windowsMeta.Capabilities);
        Assert.AreEqual(2, windowsMeta.Capabilities.Length);
        Assert.AreEqual("documentsLibrary", windowsMeta.Capabilities[0]);
        Assert.AreEqual("downloadsFolder", windowsMeta.Capabilities[1]);
    }

    [TestMethod]
    public void McpbManifest_GetWindowsMeta_ReturnsNull_WhenNoMeta()
    {
        var json = """
        {
            "name": "TestServer",
            "version": "1.0.0",
            "description": "Test",
            "server": { "type": "binary", "entry_point": "test.exe" }
        }
        """;

        var manifest = JsonSerializer.Deserialize(json, McpbJsonContext.Default.McpbManifest);
        Assert.IsNotNull(manifest);
        Assert.IsNull(manifest.GetWindowsMeta());
    }

    #endregion

    #region End-to-End Staging Tests

    [TestMethod]
    public async Task ExtractAndPrepare_ValidMcpb_CreatesCorrectStagingLayout()
    {
        // Arrange: create a valid .mcpb file
        var mcpbPath = CreateSampleMcpb("TestServer", "1.2.0");
        var service = new McpbService();
        var taskContext = TestHelpers.CreateTaskContext();

        // Act
        var result = await service.ExtractAndPrepareAsync(
            new FileInfo(mcpbPath),
            architecture: "x64",
            publisher: "CN=Test",
            runtimePath: null,
            taskContext,
            CancellationToken.None);

        try
        {
            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual("TestServer", result.PackageName);
            Assert.AreEqual("1.2.0.0", result.PackageVersion);
            Assert.AreEqual("TestServer", result.DisplayName);
            Assert.AreEqual("TestServer.exe", result.EntryPointExe);
            Assert.IsTrue(result.StagingDirectory.Exists);

            // Check AppxManifest.xml exists and contains expected values
            var manifestPath = Path.Combine(result.StagingDirectory.FullName, "AppxManifest.xml");
            Assert.IsTrue(File.Exists(manifestPath));
            var manifestContent = await File.ReadAllTextAsync(manifestPath);
            Assert.IsTrue(manifestContent.Contains("Name=\"TestServer\""));
            Assert.IsTrue(manifestContent.Contains("Version=\"1.2.0.0\""));
            Assert.IsTrue(manifestContent.Contains("Publisher=\"CN=Test\""));
            Assert.IsTrue(manifestContent.Contains("ProcessorArchitecture=\"x64\""));
            Assert.IsTrue(manifestContent.Contains("com.microsoft.windows.ai.mcpServer"));
            Assert.IsTrue(manifestContent.Contains("TrustedLaunch"));
            Assert.IsTrue(manifestContent.Contains("Alias=\"TestServer.exe\""));

            // Check Assets directory
            var assetsDir = Path.Combine(result.StagingDirectory.FullName, "Assets");
            Assert.IsTrue(Directory.Exists(assetsDir));
            Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "manifest.json")));

            // Check server executable was staged
            Assert.IsTrue(File.Exists(Path.Combine(result.StagingDirectory.FullName, "TestServer.exe")));
        }
        finally
        {
            // Clean up
            if (result.StagingDirectory.Exists)
            {
                result.StagingDirectory.Delete(recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ExtractAndPrepare_MissingStaticResponses_ThrowsValidationError()
    {
        // Arrange: create .mcpb without static_responses
        var mcpbPath = CreateMcpbWithManifest("""
        {
            "name": "BadServer",
            "version": "1.0.0",
            "description": "Missing static responses",
            "server": { "type": "binary", "entry_point": "BadServer.exe" }
        }
        """, includeExe: true, exeName: "BadServer.exe");

        var service = new McpbService();
        var taskContext = TestHelpers.CreateTaskContext();

        // Act & Assert
        try
        {
            await service.ExtractAndPrepareAsync(
                new FileInfo(mcpbPath), "x64", "CN=Test", null, taskContext, CancellationToken.None);
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException ex)
        {
            Assert.IsTrue(ex.Message.Contains("static_responses"));
        }
    }

    [TestMethod]
    public async Task ExtractAndPrepare_MissingRequiredFields_ThrowsValidationError()
    {
        // Arrange: create .mcpb missing 'name'
        var mcpbPath = CreateMcpbWithManifest("""
        {
            "version": "1.0.0",
            "description": "Missing name field",
            "server": { "type": "binary", "entry_point": "test.exe" },
            "_meta": {
                "com.microsoft.windows": {
                    "static_responses": {
                        "initialize": {},
                        "tools/list": {}
                    }
                }
            }
        }
        """, includeExe: true, exeName: "test.exe");

        var service = new McpbService();
        var taskContext = TestHelpers.CreateTaskContext();

        try
        {
            await service.ExtractAndPrepareAsync(
                new FileInfo(mcpbPath), "x64", "CN=Test", null, taskContext, CancellationToken.None);
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException ex)
        {
            Assert.IsTrue(ex.Message.Contains("name"));
        }
    }

    [TestMethod]
    public async Task ExtractAndPrepare_MissingEntryPointFile_ThrowsValidationError()
    {
        // Arrange: create .mcpb where entry_point file doesn't exist in archive
        var mcpbPath = CreateMcpbWithManifest("""
        {
            "name": "MissingExe",
            "version": "1.0.0",
            "description": "Entry point exe not in archive",
            "server": { "type": "binary", "entry_point": "NonExistent.exe" },
            "_meta": {
                "com.microsoft.windows": {
                    "static_responses": {
                        "initialize": {},
                        "tools/list": {}
                    }
                }
            }
        }
        """, includeExe: false);

        var service = new McpbService();
        var taskContext = TestHelpers.CreateTaskContext();

        try
        {
            await service.ExtractAndPrepareAsync(
                new FileInfo(mcpbPath), "x64", "CN=Test", null, taskContext, CancellationToken.None);
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException ex)
        {
            Assert.IsTrue(ex.Message.Contains("Entry point"));
        }
    }

    [TestMethod]
    public async Task ExtractAndPrepare_Arm64Architecture_SetsArchitectureInManifest()
    {
        var mcpbPath = CreateSampleMcpb("ArmServer", "1.0.0");
        var service = new McpbService();
        var taskContext = TestHelpers.CreateTaskContext();

        var result = await service.ExtractAndPrepareAsync(
            new FileInfo(mcpbPath), "arm64", "CN=Test", null, taskContext, CancellationToken.None);

        try
        {
            var manifestContent = await File.ReadAllTextAsync(
                Path.Combine(result.StagingDirectory.FullName, "AppxManifest.xml"));
            Assert.IsTrue(manifestContent.Contains("ProcessorArchitecture=\"arm64\""));
        }
        finally
        {
            if (result.StagingDirectory.Exists)
            {
                result.StagingDirectory.Delete(recursive: true);
            }
        }
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Creates a valid sample .mcpb file with all required fields.
    /// </summary>
    private string CreateSampleMcpb(string name, string version)
    {
        var manifest = $$"""
        {
            "manifest_version": "0.1",
            "name": "{{name}}",
            "version": "{{version}}",
            "description": "A test MCP server",
            "author": { "name": "TestAuthor" },
            "server": {
                "type": "binary",
                "entry_point": "{{name}}.exe"
            },
            "_meta": {
                "com.microsoft.windows": {
                    "static_responses": {
                        "initialize": { "protocolVersion": "2025-06-18" },
                        "tools/list": { "tools": [] }
                    }
                }
            }
        }
        """;

        return CreateMcpbWithManifest(manifest, includeExe: true, exeName: $"{name}.exe");
    }

    /// <summary>
    /// Creates a .mcpb (ZIP) file with the given manifest.json content.
    /// </summary>
    private string CreateMcpbWithManifest(string manifestJson, bool includeExe = false, string exeName = "server.exe")
    {
        var mcpbPath = Path.Combine(_tempDir.FullName, $"test-{Guid.NewGuid():N}.mcpb");
        var sourceDir = Path.Combine(_tempDir.FullName, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "manifest.json"), manifestJson, Encoding.UTF8);

        if (includeExe)
        {
            // Create a dummy executable file
            File.WriteAllText(Path.Combine(sourceDir, exeName), "dummy-exe-content");
        }

        ZipFile.CreateFromDirectory(sourceDir, mcpbPath);
        Directory.Delete(sourceDir, recursive: true);

        return mcpbPath;
    }

    #endregion
}

/// <summary>
/// Helper utilities for creating test infrastructure.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Creates a minimal TaskContext for testing that captures debug/status messages.
    /// </summary>
    internal static TaskContext CreateTaskContext()
    {
        var testConsole = new Spectre.Console.Testing.TestConsole();
        var task = new GroupableTask("Test", null);
        var renderLock = new Lock();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        return new TaskContext(task, null, testConsole, logger, renderLock);
    }
}

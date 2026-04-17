// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class NewCommandTests : BaseCommandTests
{
    [TestMethod]
    public async Task CliSchema_ShouldContainNewCommand()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        Assert.AreEqual(0, exitCode);
        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;
        Assert.IsTrue(root.TryGetProperty("subcommands", out var subcommands));
        Assert.IsTrue(subcommands.TryGetProperty("new", out var newCmd),
            "Schema should contain 'new' subcommand");

        // Verify it has the expected options
        Assert.IsTrue(newCmd.TryGetProperty("options", out var options));
        Assert.IsTrue(options.TryGetProperty("--name", out _), "new command should have --name option");
        Assert.IsTrue(options.TryGetProperty("--output", out _), "new command should have --output option");
        Assert.IsTrue(options.TryGetProperty("--project", out _), "new command should have --project option");

        // Verify it has the template argument
        Assert.IsTrue(newCmd.TryGetProperty("arguments", out var arguments));
        Assert.IsTrue(arguments.TryGetProperty("template", out _), "new command should have 'template' argument");
    }

    [TestMethod]
    public void TemplateJson_ShouldDeserializeCorrectly()
    {
        var json = """
        {
            "name": "WinUI Blank App",
            "shortName": "winui",
            "description": "Creates a WinUI 3 desktop app.",
            "tags": { "type": "project", "language": "C#" },
            "symbols": {
                "dotnetVersion": {
                    "type": "parameter",
                    "datatype": "choice",
                    "description": "Target framework",
                    "defaultValue": "net10.0",
                    "choices": [
                        { "choice": "net8.0", "description": "Target .NET 8" },
                        { "choice": "net10.0", "description": "Target .NET 10" }
                    ]
                },
                "publisherDn": {
                    "type": "parameter",
                    "datatype": "text",
                    "description": "Publisher DN",
                    "defaultValue": "CN=AppPublisher"
                },
                "UseLatestWindowsAppSDK": {
                    "type": "parameter",
                    "datatype": "bool",
                    "defaultValue": "true"
                },
                "safeProjectName": {
                    "type": "derived",
                    "valueSource": "name"
                }
            }
        }
        """;

        var template = JsonSerializer.Deserialize(json, TemplateJsonContext.Default.TemplateJson);

        Assert.IsNotNull(template);
        Assert.AreEqual("WinUI Blank App", template.Name);
        Assert.AreEqual("winui", template.ShortName);
        Assert.AreEqual("project", template.Tags?.Type);
        Assert.IsNotNull(template.Symbols);
        Assert.AreEqual(4, template.Symbols.Count);

        // Verify parameter symbol
        Assert.IsTrue(template.Symbols.TryGetValue("dotnetVersion", out var dotnetSymbol));
        Assert.AreEqual("parameter", dotnetSymbol.Type);
        Assert.AreEqual("choice", dotnetSymbol.DataType);
        Assert.AreEqual(2, dotnetSymbol.Choices?.Count);
        Assert.AreEqual("net8.0", dotnetSymbol.Choices?[0].Choice);

        // Verify text symbol
        Assert.IsTrue(template.Symbols.TryGetValue("publisherDn", out var pubSymbol));
        Assert.AreEqual("text", pubSymbol.DataType);
        Assert.AreEqual("CN=AppPublisher", pubSymbol.DefaultValue);

        // Verify bool symbol
        Assert.IsTrue(template.Symbols.TryGetValue("UseLatestWindowsAppSDK", out var boolSymbol));
        Assert.AreEqual("bool", boolSymbol.DataType);

        // Verify derived symbol exists but is not "parameter" type
        Assert.IsTrue(template.Symbols.TryGetValue("safeProjectName", out var derivedSymbol));
        Assert.AreEqual("derived", derivedSymbol.Type);
    }

    [TestMethod]
    public void TemplateJson_ShouldHandleTrailingCommas()
    {
        // Real Microsoft template.json files contain trailing commas
        var json = """
        {
            "name": "Test",
            "shortName": "test",
            "tags": { "type": "project", },
            "symbols": {
                "param1": {
                    "type": "parameter",
                    "datatype": "text",
                },
            },
        }
        """;

        var template = JsonSerializer.Deserialize(json, TemplateJsonContext.Default.TemplateJson);

        Assert.IsNotNull(template);
        Assert.AreEqual("Test", template.Name);
        Assert.AreEqual("test", template.ShortName);
    }

    [TestMethod]
    public void TemplateJson_ShouldHandleMissingOptionalFields()
    {
        // Minimal template.json with only required fields
        var json = """
        {
            "name": "Minimal",
            "shortName": "min"
        }
        """;

        var template = JsonSerializer.Deserialize(json, TemplateJsonContext.Default.TemplateJson);

        Assert.IsNotNull(template);
        Assert.AreEqual("Minimal", template.Name);
        Assert.IsNull(template.Description);
        Assert.IsNull(template.Tags);
        Assert.IsNull(template.Symbols);
    }

    [TestMethod]
    public void DotNetTemplateProvider_ShouldReadTemplatesFromNupkg()
    {
        var nupkgPath = Path.Combine(_tempDirectory.FullName, "TestTemplates.1.0.0.nupkg");
        CreateTestNupkg(nupkgPath, [
            ("content/testtemplate/.template.config/template.json", """
            {
                "name": "Test Template",
                "shortName": "test-tpl",
                "description": "A test template",
                "tags": { "type": "project", "language": "C#" },
                "symbols": {
                    "framework": {
                        "type": "parameter",
                        "datatype": "choice",
                        "description": "Framework",
                        "choices": [
                            { "choice": "net8.0", "description": ".NET 8" },
                            { "choice": "net10.0", "description": ".NET 10" }
                        ]
                    },
                    "computed": {
                        "type": "generated",
                        "generator": "guid"
                    }
                }
            }
            """),
            ("content/testitem/.template.config/template.json", """
            {
                "name": "Test Item",
                "shortName": "test-item",
                "description": "A test item template",
                "tags": { "type": "item", "language": "C#" },
                "symbols": {}
            }
            """)
        ]);

        var templates = ReadTemplatesFromNupkgViaReflection(nupkgPath);

        Assert.AreEqual(2, templates.Count);

        // Verify project template
        var projectTemplate = templates.First(t => t.Type == TemplateType.Project);
        Assert.AreEqual("Test Template", projectTemplate.Name);
        Assert.AreEqual("test-tpl", projectTemplate.ShortName);
        Assert.AreEqual("C#", projectTemplate.Language);
        Assert.AreEqual(1, projectTemplate.Parameters.Count); // Only "parameter" type, not "generated"
        Assert.AreEqual("framework", projectTemplate.Parameters[0].Name);
        Assert.AreEqual(TemplateParameterDataType.Choice, projectTemplate.Parameters[0].DataType);
        Assert.AreEqual(2, projectTemplate.Parameters[0].Choices?.Count);

        // Verify item template
        var itemTemplate = templates.First(t => t.Type == TemplateType.Item);
        Assert.AreEqual("Test Item", itemTemplate.Name);
        Assert.AreEqual("test-item", itemTemplate.ShortName);
        Assert.AreEqual("C#", itemTemplate.Language);
        Assert.AreEqual(0, itemTemplate.Parameters.Count);
    }

    [TestMethod]
    public void DotNetTemplateProvider_ShouldSkipInvalidTemplates()
    {
        // Templates with missing name or shortName should be skipped
        var nupkgPath = Path.Combine(_tempDirectory.FullName, "InvalidTemplates.1.0.0.nupkg");
        CreateTestNupkg(nupkgPath, [
            // Valid template
            ("content/valid/.template.config/template.json", """
            { "name": "Valid", "shortName": "valid", "tags": { "type": "project" } }
            """),
            // Missing shortName
            ("content/no-shortname/.template.config/template.json", """
            { "name": "No ShortName" }
            """),
            // Missing name
            ("content/no-name/.template.config/template.json", """
            { "shortName": "no-name" }
            """)
        ]);

        var templates = ReadTemplatesFromNupkgViaReflection(nupkgPath);

        // Only the valid template should be returned
        Assert.AreEqual(1, templates.Count);
        Assert.AreEqual("Valid", templates[0].Name);
    }

    [TestMethod]
    public void DotNetTemplateProvider_ShouldHandleTrailingCommasInNupkg()
    {
        // Real WinUI template.json files have trailing commas in the symbols section
        var nupkgPath = Path.Combine(_tempDirectory.FullName, "TrailingComma.1.0.0.nupkg");
        CreateTestNupkg(nupkgPath, [
            ("content/tc/.template.config/template.json", """
            {
                "name": "Trailing Comma",
                "shortName": "tc",
                "tags": { "type": "project", },
                "symbols": {
                    "param1": {
                        "type": "parameter",
                        "datatype": "bool",
                        "defaultValue": "true",
                    },
                },
            }
            """)
        ]);

        var templates = ReadTemplatesFromNupkgViaReflection(nupkgPath);

        Assert.AreEqual(1, templates.Count);
        Assert.AreEqual("Trailing Comma", templates[0].Name);
        Assert.AreEqual(1, templates[0].Parameters.Count);
    }

    [TestMethod]
    public async Task CompositeTemplateService_ShouldAggregateProviders()
    {
        // Create a fake provider with known templates
        var provider = new FakeTemplateProvider("TestLang", [
            new TemplateInfo("Proj1", "proj1", "A project", TemplateType.Project, "TestLang", []),
            new TemplateInfo("Item1", "item1", "An item", TemplateType.Item, "TestLang", []),
            new TemplateInfo("Proj2", "proj2", "Another project", TemplateType.Project, "TestLang", [])
        ]);

        var service = new TemplateService([provider]);

        var all = await service.GetAvailableTemplatesAsync();
        Assert.AreEqual(3, all.Count);

        var projects = await service.GetProjectTemplatesAsync();
        Assert.AreEqual(2, projects.Count);
        Assert.IsTrue(projects.All(t => t.Type == TemplateType.Project));

        var items = await service.GetItemTemplatesAsync();
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("Item1", items[0].Name);
    }

    [TestMethod]
    public async Task CompositeTemplateService_ShouldRouteCreateToCorrectProvider()
    {
        var provider1 = new FakeTemplateProvider("Lang1", [
            new TemplateInfo("T1", "t1", "Template 1", TemplateType.Project, "Lang1", [])
        ]);
        var provider2 = new FakeTemplateProvider("Lang2", [
            new TemplateInfo("T2", "t2", "Template 2", TemplateType.Project, "Lang2", [])
        ]);

        var service = new TemplateService([provider1, provider2]);

        // Creating t1 should route to provider1
        var result = await service.CreateFromTemplateAsync("t1", "MyApp", null, null, null, null);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, provider1.CreateCallCount);
        Assert.AreEqual(0, provider2.CreateCallCount);

        // Creating t2 should route to provider2
        result = await service.CreateFromTemplateAsync("t2", "MyApp", null, null, null, null);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, provider1.CreateCallCount);
        Assert.AreEqual(1, provider2.CreateCallCount);
    }

    [TestMethod]
    public async Task CompositeTemplateService_ShouldReturnErrorForUnknownTemplate()
    {
        var provider = new FakeTemplateProvider("TestLang", [
            new TemplateInfo("T1", "t1", "Template", TemplateType.Project, "TestLang", [])
        ]);

        var service = new TemplateService([provider]);

        var result = await service.CreateFromTemplateAsync("nonexistent", "MyApp", null, null, null, null);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("No template provider found", result.Error);
    }

    [TestMethod]
    public async Task NewCommand_WithoutTemplate_InNonInteractive_ShouldFail()
    {
        // Arrange - no template specified, output is redirected (non-interactive in tests)
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "new" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert - should fail because template is required when not interactive
        Assert.AreNotEqual(0, exitCode);
    }

    [TestMethod]
    public void GetUniqueDefaultName_ShouldReturnBaseNameWhenNoConflict()
    {
        var name = InvokeGetUniqueDefaultName("winui-app", _tempDirectory, isItem: false);
        Assert.AreEqual("winui-app", name);
    }

    [TestMethod]
    public void GetUniqueDefaultName_ShouldIncrementForExistingDirectories()
    {
        // Create directories to simulate existing projects
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "winui-app"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "winui-app2"));

        var name = InvokeGetUniqueDefaultName("winui-app", _tempDirectory, isItem: false);
        Assert.AreEqual("winui-app3", name);
    }

    [TestMethod]
    public void GetUniqueDefaultName_ShouldIncrementForExistingItems()
    {
        // Create .xaml files to simulate existing items
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "NewItem.xaml"), "<Page />");

        var name = InvokeGetUniqueDefaultName("NewItem", _tempDirectory, isItem: true);
        Assert.AreEqual("NewItem2", name);
    }

    [TestMethod]
    public void GetUniqueDefaultName_ItemWithNoConflict()
    {
        var name = InvokeGetUniqueDefaultName("SettingsPage", _tempDirectory, isItem: true);
        Assert.AreEqual("SettingsPage", name);
    }

    /// <summary>
    /// Creates a minimal .nupkg (zip) file with the given entries.
    /// </summary>
    private static void CreateTestNupkg(string path, (string EntryPath, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    /// <summary>
    /// Reads templates from a nupkg using reflection to access the private static method.
    /// </summary>
    private static IReadOnlyList<TemplateInfo> ReadTemplatesFromNupkgViaReflection(string nupkgPath)
    {
        var method = typeof(DotNetTemplateProvider).GetMethod(
            "ReadTemplatesFromNupkg",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method, "ReadTemplatesFromNupkg method should exist");

        var result = method.Invoke(null, [nupkgPath]);
        Assert.IsNotNull(result);
        return (IReadOnlyList<TemplateInfo>)result;
    }

    /// <summary>
    /// Invokes the private static GetUniqueDefaultName method via reflection.
    /// </summary>
    private static string InvokeGetUniqueDefaultName(string baseName, DirectoryInfo directory, bool isItem)
    {
        var handlerType = typeof(NewCommand).GetNestedType("Handler", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(handlerType, "Handler nested type should exist");

        var method = handlerType.GetMethod(
            "GetUniqueDefaultName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method, "GetUniqueDefaultName method should exist");

        var result = method.Invoke(null, [baseName, directory, isItem]);
        Assert.IsNotNull(result);
        return (string)result;
    }

    /// <summary>
    /// Fake template provider for testing the composite TemplateService.
    /// </summary>
    private class FakeTemplateProvider(string language, IReadOnlyList<TemplateInfo> templates) : ITemplateProvider
    {
        public string Language => language;
        public int CreateCallCount { get; private set; }

        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TemplateInfo>> GetTemplatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(templates);

        public Task<(int ExitCode, string Output, string Error)> CreateAsync(
            string shortName, string name, DirectoryInfo? outputDir, FileInfo? projectFile,
            IReadOnlyDictionary<string, string>? parameters, IReadOnlyList<string>? extraArgs,
            CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            return Task.FromResult((0, $"Created {shortName} '{name}'", string.Empty));
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestAddAliasCommandTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IDevModeService, FakeDevModeService>();
    }

    #region Command registration tests

    [TestMethod]
    public void ManifestCommandShouldHaveAddAliasSubcommand()
    {
        // Arrange & Act
        var manifestCommand = GetRequiredService<ManifestCommand>();

        // Assert
        Assert.IsTrue(manifestCommand.Subcommands.Any(c => c.Name == "add-alias"), "Should have 'add-alias' subcommand");
    }

    [TestMethod]
    public void AddAliasCommandShouldHaveExpectedOptions()
    {
        // Arrange & Act
        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Assert
        Assert.IsNotNull(command, "ManifestAddAliasCommand should be created");
        Assert.AreEqual("add-alias", command.Name);
        Assert.IsTrue(command.Options.Any(o => o.Name == "--name"), "Should have --name option");
        Assert.IsTrue(command.Options.Any(o => o.Name == "--manifest"), "Should have --manifest option");
        Assert.IsTrue(command.Options.Any(o => o.Name == "--app-id"), "Should have --app-id option");
    }

    #endregion

    #region Fresh alias addition tests

    [TestMethod]
    public async Task AddAlias_FreshManifestNoExtensions_AddsAliasSuccessfully()
    {
        // Arrange - manifest with no Extensions block
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("uap5:ExecutionAlias", content, "Should contain ExecutionAlias element");
        Assert.Contains("Alias=\"myapp.exe\"", content, "Alias should match the Executable attribute");
        Assert.Contains("uap5:Extension", content, "Should contain Extension element");
        Assert.Contains("uap5:AppExecutionAlias", content, "Should contain AppExecutionAlias element");
        Assert.Contains("windows.appExecutionAlias", content, "Should contain correct category");
        Assert.Contains("xmlns:uap5=", content, "Should add uap5 namespace declaration");
    }

    [TestMethod]
    public async Task AddAlias_WithCustomName_UsesCustomName()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", "custom-alias.exe"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"custom-alias.exe\"", content, "Alias should use the custom name");
    }

    [TestMethod]
    public async Task AddAlias_NameWithoutExeExtension_AppendsExe()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", "myapp"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"myapp.exe\"", content, "Alias should have .exe appended");
    }

    [TestMethod]
    public async Task AddAlias_WithTargetNameToken_InfersTokenAsAlias()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="$targetnametoken$.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"$targetnametoken$.exe\"", content, "Alias should preserve $targetnametoken$ placeholder");
    }

    #endregion

    #region Idempotent / error case tests

    [TestMethod]
    public async Task AddAlias_SameAliasAlreadyExists_ReturnsSuccessWithWarning()
    {
        // Arrange - manifest with existing alias
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="myapp.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert - idempotent: same alias returns 0
        Assert.AreEqual(0, exitCode, "Command should succeed when same alias already exists");
    }

    [TestMethod]
    public async Task AddAlias_DifferentAliasAlreadyExists_ReturnsError()
    {
        // Arrange - manifest with a different existing alias
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="existing-alias.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", "different.exe"]);

        // Assert - should error when a different alias exists
        Assert.AreEqual(1, exitCode, "Command should fail when a different alias already exists");
    }

    #endregion

    #region App ID selection tests

    [TestMethod]
    public async Task AddAlias_WithAppId_TargetsCorrectApplication()
    {
        // Arrange - manifest with multiple Application elements
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="App1" Executable="app1.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="App2" Executable="app2.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "App2"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"app2.exe\"", content, "Alias should be inferred from App2's Executable");
    }

    [TestMethod]
    public async Task AddAlias_WithInvalidAppId_ReturnsError()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "NonExistent"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail for non-existent app ID");
    }

    #endregion

    #region Error handling tests

    [TestMethod]
    public async Task AddAlias_NoManifestFound_ReturnsError()
    {
        // Arrange - no manifest in the temp directory
        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - no --manifest and no manifest in cwd
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when no manifest is found");
    }

    [TestMethod]
    public async Task AddAlias_ManifestNoApplicationElement_ReturnsError()
    {
        // Arrange - minimal manifest without Applications
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test" Publisher="CN=test" Version="1.0.0.0" />
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when no Application element exists");
    }

    [TestMethod]
    public async Task AddAlias_NoExecutableAndNoName_ReturnsError()
    {
        // Arrange - Application without Executable attribute
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when Executable attr missing and --name not specified");
    }

    #endregion

    #region Namespace handling tests

    [TestMethod]
    public async Task AddAlias_Uap5NamespaceAlreadyDeclared_DoesNotDuplicate()
    {
        // Arrange - uap5 already declared
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        // Count occurrences - should only have one uap5 namespace declaration
        var count = content.Split("xmlns:uap5=").Length - 1;
        Assert.AreEqual(1, count, "Should have exactly one uap5 namespace declaration");
    }

    [TestMethod]
    public async Task AddAlias_AddsUap5ToIgnorableNamespaces()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("uap5", content, "IgnorableNamespaces should include uap5");
    }

    #endregion

    #region XML formatting tests

    [TestMethod]
    public async Task AddAlias_OutputXmlIsWellFormed()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        // Verify the output is well-formed XML by parsing it again
        var content = await File.ReadAllTextAsync(manifestPath);
        var doc = System.Xml.Linq.XDocument.Parse(content);
        Assert.IsNotNull(doc.Root, "Output should be valid XML");
    }

    [TestMethod]
    public async Task AddAlias_ElementsWithManyAttributes_AreFormattedOnSeparateLines()
    {
        // Arrange - manifest with elements that have 3+ attributes
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication"
                             uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="packagedClassicApp">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var lines = (await File.ReadAllTextAsync(manifestPath)).Split('\n');

        // Find the <Application line (should have 5+ attrs and be split across lines)
        var applicationLineIdx = Array.FindIndex(lines, l =>
        {
            var trimmed = l.TrimStart();
            return trimmed.StartsWith("<Application", StringComparison.Ordinal) && !trimmed.StartsWith("<Applications", StringComparison.Ordinal);
        });
        Assert.IsTrue(applicationLineIdx >= 0, "Should find <Application element");

        // With 5 attributes, the element should span multiple lines.
        // Next line should be an attribute (indented continuation), not a closing tag or child element
        var nextLine = lines[applicationLineIdx + 1].Trim();
        Assert.IsTrue(
            nextLine.StartsWith("Id=", StringComparison.Ordinal) ||
            nextLine.StartsWith("Executable=", StringComparison.Ordinal) ||
            nextLine.StartsWith("EntryPoint=", StringComparison.Ordinal) ||
            nextLine.StartsWith("uap10:", StringComparison.Ordinal),
            $"Next line after <Application should be an attribute on its own line, got: '{nextLine}'");
    }

    #endregion

    #region Helper methods

    private string CreateManifest(string content)
    {
        var path = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        File.WriteAllText(path, content);
        return path;
    }

    #endregion
}

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
        Assert.IsTrue(command.Options.Any(o => o.Name == "--update-csproj"), "Should have --update-csproj option");
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

    [TestMethod]
    public async Task AddAlias_MultipleApps_NoAppId_TargetsFirstApplication()
    {
        // Arrange - manifest with multiple Application elements, no --app-id
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="First" Executable="first.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="Second" Executable="second.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - no --app-id specified, should default to first Application
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"first.exe\"", content, "Alias should be inferred from the first Application's Executable");

        // The alias should be inside the first Application, not the second
        var doc = System.Xml.Linq.XDocument.Parse(content);
        var ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var firstApp = doc.Descendants(System.Xml.Linq.XName.Get("Application", ns)).First();
        Assert.AreEqual("First", firstApp.Attribute("Id")?.Value);
        Assert.IsNotNull(firstApp.Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "Extensions should be added to the first Application");

        var secondApp = doc.Descendants(System.Xml.Linq.XName.Get("Application", ns)).Last();
        Assert.IsNull(secondApp.Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "Second Application should not have Extensions");
    }

    [TestMethod]
    public async Task AddAlias_MultipleApps_WithAppId_OnlyModifiesTargetApp()
    {
        // Arrange - manifest with multiple apps, target the second one
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
                <Application Id="App3" Executable="app3.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - target App2
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "App2"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var doc = System.Xml.Linq.XDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var apps = doc.Descendants(System.Xml.Linq.XName.Get("Application", ns)).ToList();

        // Only App2 should have Extensions
        Assert.IsNull(apps[0].Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "App1 should not have Extensions");
        Assert.IsNotNull(apps[1].Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "App2 should have Extensions with the alias");
        Assert.IsNull(apps[2].Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "App3 should not have Extensions");
    }

    [TestMethod]
    public async Task AddAlias_MultipleApps_ExistingAliasOnOtherApp_DoesNotConflict()
    {
        // Arrange - App1 already has an alias, adding alias to App2 should work
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="App1" Executable="app1.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="app1.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
                <Application Id="App2" Executable="app2.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - add alias to App2 (App1 already has one, but that shouldn't matter)
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "App2"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed — alias on other app should not conflict");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"app1.exe\"", content, "App1's existing alias should remain");
        Assert.Contains("Alias=\"app2.exe\"", content, "App2's new alias should be added");
    }

    [TestMethod]
    public async Task AddAlias_MultipleApps_CustomNameOnSpecificApp()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="MainApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="HelperApp" Executable="helper.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - custom alias name on the second app
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "HelperApp", "--name", "my-helper"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"my-helper.exe\"", content, "Custom alias should be applied to HelperApp");
        // Ensure MainApp wasn't touched
        Assert.IsFalse(content.Contains("Alias=\"myapp.exe\""),
            "MainApp should not have received an alias");
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

    #region Alias name validation tests

    [TestMethod]
    [DataRow("..\\evil.exe", DisplayName = "parent traversal")]
    [DataRow("dir\\evil.exe", DisplayName = "path separator")]
    [DataRow("C:\\Windows\\System32\\calc.exe", DisplayName = "absolute path")]
    [DataRow("evil:test.exe", DisplayName = "colon / ADS")]
    [DataRow("evil*.exe", DisplayName = "invalid char (asterisk)")]
    [DataRow("CON.exe", DisplayName = "reserved DOS device name")]
    [DataRow("NUL.exe", DisplayName = "reserved DOS device name (NUL)")]
    [DataRow("COM1.exe", DisplayName = "reserved DOS device name (COM1)")]
    public async Task AddAlias_UnsafeName_RejectsAndDoesNotModifyManifest(string unsafeAlias)
    {
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
        var originalContent = await File.ReadAllTextAsync(manifestPath);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", unsafeAlias]);

        Assert.AreEqual(1, exitCode, $"Command should fail for unsafe alias '{unsafeAlias}'");
        var currentContent = await File.ReadAllTextAsync(manifestPath);
        Assert.AreEqual(originalContent, currentContent, "Manifest must not be modified when alias is rejected");
    }

    [TestMethod]
    public async Task AddAlias_UnsafeExecutableInferred_RejectsAndDoesNotModifyManifest()
    {
        // Executable attribute itself is the attack vector here — it gets used
        // as the alias name when --name is not supplied.
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="..\evil.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);
        var originalContent = await File.ReadAllTextAsync(manifestPath);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        Assert.AreEqual(1, exitCode, "Command should fail when inferred alias is unsafe");
        var currentContent = await File.ReadAllTextAsync(manifestPath);
        Assert.AreEqual(originalContent, currentContent, "Manifest must not be modified when inferred alias is rejected");
    }

    [TestMethod]
    [DataRow("app\\my-app.exe", "my-app.exe", DisplayName = "backslash subdir (Electron pattern)")]
    [DataRow("app/my-app.exe", "my-app.exe", DisplayName = "forward slash subdir")]
    [DataRow("bin\\sub\\nested.exe", "nested.exe", DisplayName = "multi-segment subdir")]
    public async Task AddAlias_PathPrefixedExecutable_InfersLeafFilenameAsAlias(string executable, string expectedAlias)
    {
        // The MSIX manifest's Application/@Executable is a package-relative path
        // (e.g. "app\my-app.exe" — Electron guide). The inferred alias must be
        // the leaf filename, not the full path.
        var manifestPath = CreateManifest($"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="{executable}" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        Assert.AreEqual(0, exitCode, $"Command should succeed for Executable='{executable}'");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains($"Alias=\"{expectedAlias}\"", content, "Alias should be the leaf filename of Executable");
    }

    #endregion

    #region Helper methods

    #region --update-csproj tests

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_AddsWinAppRunUseExecutionAliasProperty()
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
        var csprojPath = CreateCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var csproj = await File.ReadAllTextAsync(csprojPath);
        Assert.Contains("<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>", csproj, "Should set the property to true");
    }

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_UpdatesExistingFalseProperty()
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
        var csprojPath = CreateCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <WinAppRunUseExecutionAlias>false</WinAppRunUseExecutionAlias>
              </PropertyGroup>
            </Project>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var csproj = await File.ReadAllTextAsync(csprojPath);
        Assert.Contains("<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>", csproj, "Should update the property to true");
        Assert.DoesNotContain("<WinAppRunUseExecutionAlias>false</WinAppRunUseExecutionAlias>", csproj, "Should not leave the false value");
    }

    [TestMethod]
    public async Task AddAlias_WithoutUpdateCsproj_DoesNotModifyCsproj()
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
        var csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        var csprojPath = CreateCsproj(csprojContent);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var csproj = await File.ReadAllTextAsync(csprojPath);
        Assert.AreEqual(csprojContent, csproj, "Csproj should be unchanged without --update-csproj");
    }

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_NoCsproj_AddsAliasButReturnsError()
    {
        // Arrange - manifest but no .csproj next to it
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
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert - alias still added, but --update-csproj could not be honored so the command reports failure
        Assert.AreEqual(1, exitCode, "Command should report failure when --update-csproj is requested but no .csproj is present");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("uap5:ExecutionAlias", content, "Should still add the execution alias");
    }

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_PropertyAlreadyTrue_LeavesCsprojUnchanged()
    {
        // Arrange
        var manifestPath = CreateDefaultManifest();
        var csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>
              </PropertyGroup>
            </Project>
            """;
        var csprojPath = CreateCsproj(csprojContent);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var csproj = await File.ReadAllTextAsync(csprojPath);
        Assert.AreEqual(csprojContent, csproj, "Csproj should be byte-for-byte unchanged when already true");
    }

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_CommentedOutProperty_DoesNotCorruptCsproj()
    {
        // Arrange - the property exists only inside an XML comment; the regex must not splice into it
        var manifestPath = CreateDefaultManifest();
        var csprojPath = CreateCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <!-- <WinAppRunUseExecutionAlias>false</WinAppRunUseExecutionAlias> -->
              </PropertyGroup>
            </Project>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var csproj = await File.ReadAllTextAsync(csprojPath);

        // The file must remain valid XML (a corrupting splice would produce nested/unbalanced comments)
        var doc = System.Xml.Linq.XDocument.Parse(csproj);
        var liveProperties = doc.Descendants()
            .Count(e => e.Name.LocalName == "WinAppRunUseExecutionAlias" && e.Value.Trim() == "true");
        Assert.AreEqual(1, liveProperties, "Should add exactly one live property set to true");
        Assert.Contains("<!-- <WinAppRunUseExecutionAlias>false</WinAppRunUseExecutionAlias> -->", csproj, "Original commented-out property should be preserved intact");
    }

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_TargetFrameworksPlural_AddsProperty()
    {
        // Arrange - no singular <TargetFramework>; insertion must fall back to <PropertyGroup>
        var manifestPath = CreateDefaultManifest();
        var csprojPath = CreateCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0-windows10.0.26100.0;net8.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var csproj = await File.ReadAllTextAsync(csprojPath);
        System.Xml.Linq.XDocument.Parse(csproj);
        Assert.Contains("<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>", csproj, "Should add the property");
    }

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_NoPropertyGroup_ReturnsError()
    {
        // Arrange - no insertion point at all
        var manifestPath = CreateDefaultManifest();
        var csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
            </Project>
            """;
        var csprojPath = CreateCsproj(csprojContent);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should report failure when there is no insertion point");
        var csproj = await File.ReadAllTextAsync(csprojPath);
        Assert.AreEqual(csprojContent, csproj, "Csproj should be unchanged when it cannot be updated");
    }

    [TestMethod]
    public async Task AddAlias_UpdateCsproj_MultipleCsproj_UpdatesAll()
    {
        // Arrange
        var manifestPath = CreateDefaultManifest();
        var csprojA = CreateCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, "app-a.csproj");
        var csprojB = CreateCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, "app-b.csproj");

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--update-csproj"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.Contains("<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>", await File.ReadAllTextAsync(csprojA), "First csproj should be updated");
        Assert.Contains("<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>", await File.ReadAllTextAsync(csprojB), "Second csproj should be updated");
    }

    [TestMethod]
    public async Task AddAlias_AlreadyExists_UpdateCsproj_SetsProperty()
    {
        // Arrange - manifest already has the alias; --update-csproj should still set the property
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap5 uap10">
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
        var csprojPath = CreateCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", "myapp.exe", "--update-csproj"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed when alias already exists");
        var csproj = await File.ReadAllTextAsync(csprojPath);
        Assert.Contains("<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>", csproj, "Should set the property even when the alias already exists");
    }

    private string CreateCsproj(string content)
        => CreateCsproj(content, "test-app.csproj");

    private string CreateCsproj(string content, string fileName)
    {
        var path = Path.Combine(_tempDirectory.FullName, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateDefaultManifest()
        => CreateManifest("""
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

    #endregion

    private string CreateManifest(string content)
    {
        var path = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        File.WriteAllText(path, content);
        return path;
    }

    #endregion
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ConfigServiceJsBindingsTests : BaseCommandTests
{
    [TestMethod]
    public void Load_NoJsBindings_ReturnsNull()
    {
        // Arrange
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            """);

        // Act
        var cfg = _configService.Load();

        // Assert
        Assert.AreEqual(1, cfg.Packages.Count);
        Assert.IsNull(cfg.JsBindings, "JsBindings must be null when block is absent");
    }

    [TestMethod]
    public void Load_MinimalJsBindings_AppliesDefaults()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual("js", cfg.JsBindings.Lang, "lang defaults to 'js'");
        Assert.AreEqual("bindings/winrt", cfg.JsBindings.Output);
        Assert.AreEqual(0, cfg.JsBindings.ExtraTypes.Count);
        // packages: defaults to empty == "all installed packages participate in
        // binding generation". Preset application narrows this list.
        Assert.AreEqual(0, cfg.JsBindings.Packages.Count,
            "Packages list defaults to empty (no preset slicing) — codegen handles Windows.* ref-classification on its own.");
    }

    [TestMethod]
    public void Load_FullJsBindings_ParsesAllFields()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              lang: js
              output: src/generated
              packages:
                - Microsoft.WindowsAppSDK.AI
              extraTypes:
                - namespace: Windows.Foundation
                  classes:
                    - Uri
                    - PropertyValue
                - namespace: Windows.Globalization
                  classes:
                    - Calendar
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual("src/generated", cfg.JsBindings.Output);
        CollectionAssert.AreEqual(
            new[] { "Microsoft.WindowsAppSDK.AI" },
            cfg.JsBindings.Packages);

        Assert.AreEqual(2, cfg.JsBindings.ExtraTypes.Count);

        Assert.AreEqual("Windows.Foundation", cfg.JsBindings.ExtraTypes[0].Namespace);
        CollectionAssert.AreEqual(new[] { "Uri", "PropertyValue" }, cfg.JsBindings.ExtraTypes[0].Classes);

        Assert.AreEqual("Windows.Globalization", cfg.JsBindings.ExtraTypes[1].Namespace);
        CollectionAssert.AreEqual(new[] { "Calendar" }, cfg.JsBindings.ExtraTypes[1].Classes);
    }

    [TestMethod]
    public void Load_ExtraTypes_InlineFlowList_Parses()
    {
        // Inline flow form: classes: [X, Y] — equivalent to a block list.
        File.WriteAllText(_configService.ConfigPath.FullName, """
            jsBindings:
              output: generated-js
              extraTypes:
                - namespace: Windows.ApplicationModel
                  classes: [LimitedAccessFeatures]
                - namespace: Windows.Storage
                  classes: [StorageFile, StorageFolder]
                - namespace: Windows.Graphics.Imaging
                  classes: [BitmapDecoder]
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual(3, cfg.JsBindings.ExtraTypes.Count);
        CollectionAssert.AreEqual(new[] { "LimitedAccessFeatures" }, cfg.JsBindings.ExtraTypes[0].Classes);
        CollectionAssert.AreEqual(new[] { "StorageFile", "StorageFolder" }, cfg.JsBindings.ExtraTypes[1].Classes);
        CollectionAssert.AreEqual(new[] { "BitmapDecoder" }, cfg.JsBindings.ExtraTypes[2].Classes);
    }

    [TestMethod]
    public void Load_ExtraTypes_ScalarSingleClass_Parses()
    {
        // Scalar form (the legacy `systemTypes:` style some users wrote):
        // classes: SingleClass  — treat as a one-item list.
        File.WriteAllText(_configService.ConfigPath.FullName, """
            jsBindings:
              output: generated-js
              extraTypes:
                - namespace: Windows.Storage
                  classes: StorageFile
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual(1, cfg.JsBindings.ExtraTypes.Count);
        CollectionAssert.AreEqual(new[] { "StorageFile" }, cfg.JsBindings.ExtraTypes[0].Classes);
    }

    [TestMethod]
    public void SaveAndLoad_RoundTripsJsBindings()
    {
        var original = new WinappConfig();
        original.SetVersion("Microsoft.WindowsAppSDK", "1.8.39");
        original.JsBindings = new JsBindingsConfig
        {
            Lang = "js",
            Output = "bindings/winrt",
            Packages = new() { "Microsoft.WindowsAppSDK.AI" },
            ExtraTypes = new()
            {
                new JsBindingsExtraType
                {
                    Namespace = "Windows.Foundation",
                    Classes = new() { "Uri" },
                },
            },
        };

        _configService.Save(original);
        var roundTrip = _configService.Load();

        Assert.IsNotNull(roundTrip.JsBindings);
        Assert.AreEqual("js", roundTrip.JsBindings.Lang);
        Assert.AreEqual("bindings/winrt", roundTrip.JsBindings.Output);
        CollectionAssert.AreEqual(
            new[] { "Microsoft.WindowsAppSDK.AI" },
            roundTrip.JsBindings.Packages,
            "Round-trip must preserve the packages slice exactly.");
        Assert.AreEqual(1, roundTrip.JsBindings.ExtraTypes.Count);
        Assert.AreEqual("Windows.Foundation", roundTrip.JsBindings.ExtraTypes[0].Namespace);
        CollectionAssert.AreEqual(new[] { "Uri" }, roundTrip.JsBindings.ExtraTypes[0].Classes);
    }

    [TestMethod]
    public void Load_PackagesAfterJsBindings_StillParsesPackages()
    {
        // The yaml block order should not matter for top-level sections.
        File.WriteAllText(_configService.ConfigPath.FullName, """
            jsBindings:
              output: bindings/winrt
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual(1, cfg.Packages.Count);
        Assert.AreEqual("Microsoft.WindowsAppSDK", cfg.Packages[0].Name);
        Assert.AreEqual("1.8.39", cfg.Packages[0].Version);
    }

    [TestMethod]
    public void Load_AdditionalWinmds_ParsesRelativeAndAbsolutePaths()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
              additionalWinmds:
                - vendor/MyCompany.Foo.winmd
                - C:\absolute\path\Other.winmd
                - sibling.winmd
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        CollectionAssert.AreEqual(
            new[]
            {
                "vendor/MyCompany.Foo.winmd",
                @"C:\absolute\path\Other.winmd",
                "sibling.winmd",
            },
            cfg.JsBindings.AdditionalWinmds,
            "AdditionalWinmds entries must round-trip in declaration order, accepting both relative and absolute paths");
    }

    [TestMethod]
    public void Load_AdditionalWinmds_DedupesCaseInsensitive()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
              additionalWinmds:
                - vendor/Foo.winmd
                - Vendor/foo.WINMD
                - vendor/Bar.winmd
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual(
            2,
            cfg.JsBindings.AdditionalWinmds.Count,
            "Duplicate paths (case-insensitive) must be deduped to keep winmd list file deterministic");
    }

    [TestMethod]
    public void SaveAndLoad_AdditionalWinmds_RoundTrips()
    {
        var original = new WinappConfig();
        original.SetVersion("Microsoft.WindowsAppSDK", "1.8.39");
        original.JsBindings = new JsBindingsConfig
        {
            Lang = "js",
            Output = "bindings/winrt",
            AdditionalWinmds = new() { "vendor/Foo.winmd", @"C:\abs\Bar.winmd" },
        };

        _configService.Save(original);
        var roundTrip = _configService.Load();

        Assert.IsNotNull(roundTrip.JsBindings);
        CollectionAssert.AreEqual(
            new[] { "vendor/Foo.winmd", @"C:\abs\Bar.winmd" },
            roundTrip.JsBindings.AdditionalWinmds,
            "additionalWinmds must round-trip declaration order intact");
    }

    // -------------------------------------------------------------------------
    // additionalRefs — same parsing rules as additionalWinmds, but flows into
    // the codegen's --ref channel, not the winmd list file. Pairs with
    // extraTypes for cherry-picking from a vendor winmd.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Load_AdditionalRefs_ParsesRelativeAndAbsolutePaths()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
              additionalRefs:
                - vendor/BigVendor.winmd
                - C:\shared\OtherCompany.SDK.winmd
              extraTypes:
                - namespace: BigVendor.Camera
                  classes:
                    - Lens
                    - Sensor
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        CollectionAssert.AreEqual(
            new[] { "vendor/BigVendor.winmd", @"C:\shared\OtherCompany.SDK.winmd" },
            cfg.JsBindings.AdditionalRefs);
        // Adjacent extraTypes block must still parse correctly when
        // additionalRefs precedes it (regression guard for parser state).
        Assert.AreEqual(1, cfg.JsBindings.ExtraTypes.Count);
        Assert.AreEqual("BigVendor.Camera", cfg.JsBindings.ExtraTypes[0].Namespace);
        CollectionAssert.AreEqual(new[] { "Lens", "Sensor" }, cfg.JsBindings.ExtraTypes[0].Classes);
    }

    [TestMethod]
    public void Load_AdditionalRefs_DedupesCaseInsensitive()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
              additionalRefs:
                - vendor/Foo.winmd
                - VENDOR/FOO.WINMD
                - vendor/Bar.winmd
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual(2, cfg.JsBindings.AdditionalRefs.Count,
            "additionalRefs must dedupe case-insensitive (parser-level guard)");
    }

    [TestMethod]
    public void SaveAndLoad_AdditionalRefs_RoundTrips()
    {
        var original = new WinappConfig();
        original.SetVersion("Microsoft.WindowsAppSDK", "1.8.39");
        original.JsBindings = new JsBindingsConfig
        {
            Lang = "js",
            Output = "bindings/winrt",
            AdditionalWinmds = new() { "vendor/Foo.winmd" },
            AdditionalRefs = new() { "vendor/BigVendor.winmd", @"C:\abs\OtherSdk.winmd" },
            ExtraTypes =
            {
                new JsBindingsExtraType
                {
                    Namespace = "BigVendor.Camera",
                    Classes = { "Lens" },
                },
            },
        };

        _configService.Save(original);
        var roundTrip = _configService.Load();

        Assert.IsNotNull(roundTrip.JsBindings);
        // Both list fields must coexist and round-trip in their declaration order.
        CollectionAssert.AreEqual(
            new[] { "vendor/Foo.winmd" },
            roundTrip.JsBindings.AdditionalWinmds);
        CollectionAssert.AreEqual(
            new[] { "vendor/BigVendor.winmd", @"C:\abs\OtherSdk.winmd" },
            roundTrip.JsBindings.AdditionalRefs);
        // Extras-types adjacent block must also survive
        Assert.AreEqual(1, roundTrip.JsBindings.ExtraTypes.Count);
        Assert.AreEqual("BigVendor.Camera", roundTrip.JsBindings.ExtraTypes[0].Namespace);
    }

    [TestMethod]
    public void Load_NoAdditionalRefs_DefaultsToEmpty()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.IsNotNull(cfg.JsBindings.AdditionalRefs);
        Assert.AreEqual(0, cfg.JsBindings.AdditionalRefs.Count);
    }

    // -------------------------------------------------------------------------
    // packages — preset slice (NuGet package IDs that the codegen run scopes
    // to). Empty list = all installed packages participate.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Load_Packages_ParsesAndDedupes()
    {
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
              packages:
                - Microsoft.WindowsAppSDK.AI
                - Microsoft.WindowsAppSDK
                - microsoft.windowsappsdk.ai
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        CollectionAssert.AreEqual(
            new[] { "Microsoft.WindowsAppSDK.AI", "Microsoft.WindowsAppSDK" },
            cfg.JsBindings.Packages,
            "Packages list must dedupe case-insensitively while preserving the first-seen casing.");
    }

    [TestMethod]
    public void SaveAndLoad_Packages_RoundTrips()
    {
        var original = new WinappConfig();
        original.SetVersion("Microsoft.WindowsAppSDK", "1.8.39");
        original.JsBindings = new JsBindingsConfig
        {
            Lang = "js",
            Output = "bindings/winrt",
            Packages = new() { "Microsoft.WindowsAppSDK.AI" },
        };

        _configService.Save(original);
        var roundTrip = _configService.Load();

        Assert.IsNotNull(roundTrip.JsBindings);
        CollectionAssert.AreEqual(
            new[] { "Microsoft.WindowsAppSDK.AI" },
            roundTrip.JsBindings.Packages);
    }

    [TestMethod]
    public void Load_NoPackages_DefaultsToEmpty()
    {
        // Empty list (NOT null) — semantics is "all installed packages participate".
        File.WriteAllText(_configService.ConfigPath.FullName, """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
            jsBindings:
              output: bindings/winrt
            """);

        var cfg = _configService.Load();

        Assert.IsNotNull(cfg.JsBindings);
        Assert.IsNotNull(cfg.JsBindings.Packages);
        Assert.AreEqual(0, cfg.JsBindings.Packages.Count);
    }

    // -------------------------------------------------------------------------
    // Save() preserves comments + unknown fields outside jsBindings:
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Save_PreservesCommentsAndUnknownFields_OutsideJsBindings()
    {
        // Round-trip test: the user has a yaml with comments, a custom
        // top-level field winapp doesn't know about, and a jsBindings: block.
        // Save() must preserve everything except the jsBindings: block itself.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        var originalYaml =
            "# Top of file comment — must survive\n"
            + "\n"
            + "packages:\n"
            + "  # inline comment near a package\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "  - name: Microsoft.Windows.CppWinRT\n"
            + "    version: 2.0.250303.1\n"
            + "\n"
            + "# A user-added top-level field winapp's model doesn't know about\n"
            + "customField:\n"
            + "  enabled: true\n"
            + "  notes: should-survive\n"
            + "\n"
            + "jsBindings:\n"
            + "  output: old/output\n"
            + "  lang: js\n";
        File.WriteAllText(configPath, originalYaml);

        var loaded = _configService.Load();
        // Mutate just jsBindings.output (simulating what add jsbindings does).
        loaded.JsBindings!.Output = "new/output";
        _configService.SaveJsBindingsOnly(loaded);

        var roundtripped = File.ReadAllText(configPath);

        // Comments preserved.
        StringAssert.Contains(roundtripped, "# Top of file comment — must survive",
            "Top-of-file comments must survive SaveJsBindingsOnly");
        StringAssert.Contains(roundtripped, "# inline comment near a package",
            "Inline comments must survive SaveJsBindingsOnly");
        StringAssert.Contains(roundtripped, "# A user-added top-level field winapp's model doesn't know about",
            "Comments above unknown fields must survive SaveJsBindingsOnly");

        // Unknown top-level field preserved verbatim.
        StringAssert.Contains(roundtripped, "customField:",
            "Unknown top-level fields must survive SaveJsBindingsOnly");
        StringAssert.Contains(roundtripped, "enabled: true",
            "Unknown fields' children must survive SaveJsBindingsOnly");
        StringAssert.Contains(roundtripped, "notes: should-survive",
            "Unknown fields' children must survive SaveJsBindingsOnly");

        // Original packages: untouched.
        StringAssert.Contains(roundtripped, "Microsoft.WindowsAppSDK", "packages: must survive SaveJsBindingsOnly");
        StringAssert.Contains(roundtripped, "Microsoft.Windows.CppWinRT");
        StringAssert.Contains(roundtripped, "version: 1.8.39");

        // jsBindings: was patched.
        StringAssert.Contains(roundtripped, "new/output",
            "jsBindings.output should reflect the in-memory mutation");
        Assert.IsFalse(roundtripped.Contains("old/output"),
            "Old jsBindings.output should be gone");
    }

    [TestMethod]
    public void SaveJsBindingsOnly_AppendsJsBindings_WhenAbsent()
    {
        // Yaml has packages: but no jsBindings: block. After in-memory
        // injection + SaveJsBindingsOnly(), the new block should be appended.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        var originalYaml =
            "# pinning my packages\n"
            + "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n";
        File.WriteAllText(configPath, originalYaml);

        var loaded = _configService.Load();
        loaded.JsBindings = new WinApp.Cli.Models.JsBindingsConfig
        {
            Output = "src/bindings",
            Lang = "js",
            Packages = { "Microsoft.WindowsAppSDK.AI" },
        };
        _configService.SaveJsBindingsOnly(loaded);

        var roundtripped = File.ReadAllText(configPath);
        StringAssert.Contains(roundtripped, "# pinning my packages",
            "Existing comments must survive append");
        StringAssert.Contains(roundtripped, "jsBindings:",
            "New jsBindings block must be appended");
        StringAssert.Contains(roundtripped, "src/bindings");
        StringAssert.Contains(roundtripped, "Microsoft.WindowsAppSDK.AI");
    }

    [TestMethod]
    public void Save_PersistsPackageVersionChanges_OverwritingExistingFile()
    {
        // Regression guard for review #3 H1: ConfigService.Save() must persist
        // ALL model state, not just jsBindings:. winapp update mutates pinned
        // versions via SetVersion(...) then calls Save() — if Save() only
        // patched jsBindings:, those version bumps would silently disappear.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        File.WriteAllText(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "  - name: Microsoft.Windows.SDK.BuildTools\n"
            + "    version: 10.0.26100.6901\n");

        var cfg = _configService.Load();
        // Simulate winapp update bumping versions.
        cfg.SetVersion("Microsoft.WindowsAppSDK", "1.9.0-newer");
        cfg.SetVersion("Microsoft.Windows.SDK.BuildTools", "10.0.99999.9");
        _configService.Save(cfg);

        var roundtripped = File.ReadAllText(configPath);
        StringAssert.Contains(roundtripped, "1.9.0-newer",
            "winapp update's version bump must persist; previously the splice-only Save() silently dropped it.");
        StringAssert.Contains(roundtripped, "10.0.99999.9");
        Assert.IsFalse(roundtripped.Contains("1.8.39"),
            "Old version string must be gone after Save()");
    }

    [TestMethod]
    public void Load_UnknownTopLevelFieldAfterJsBindings_IsNotAbsorbed()
    {
        // Regression for review #3 M5: an unknown zero-indent key after
        // jsBindings: must not have its children parsed as JS-binding content.
        var yaml =
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n"
            + "customField:\n"
            + "  output: should-not-clobber-jsbindings-output\n"
            + "  packages:\n"
            + "    - Should.Not.Appear.In.JsBindings.Packages\n";
        File.WriteAllText(_configService.ConfigPath.FullName, yaml);

        var loaded = _configService.Load();

        Assert.IsNotNull(loaded.JsBindings);
        Assert.AreEqual("bindings/winrt", loaded.JsBindings!.Output,
            "Unknown top-level key must NOT overwrite jsBindings.output");
        CollectionAssert.AreEqual(
            new[] { "Microsoft.WindowsAppSDK.AI" },
            loaded.JsBindings.Packages.ToList(),
            "Unknown top-level key's list children must NOT leak into jsBindings.packages");
    }

    [TestMethod]
    public void SaveJsBindingsOnly_PreservesTopLevelCommentAfterJsBindingsBlock()
    {
        // Regression: a zero-indent `# comment` between jsBindings: and the
        // next top-level key must survive the splice (it belongs to the next
        // section, not jsBindings).
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        File.WriteAllText(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: old/output\n"
            + "  lang: js\n"
            + "\n"
            + "# IMPORTANT comment about customField (must survive splice)\n"
            + "customField:\n"
            + "  value: 42\n");

        var loaded = _configService.Load();
        loaded.JsBindings!.Output = "new/output";
        _configService.SaveJsBindingsOnly(loaded);

        var roundtripped = File.ReadAllText(configPath);
        StringAssert.Contains(roundtripped, "# IMPORTANT comment about customField",
            "Zero-indent comment between jsBindings: and the next top-level key must survive splice.");
        StringAssert.Contains(roundtripped, "customField:");
        StringAssert.Contains(roundtripped, "new/output");
        Assert.IsFalse(roundtripped.Contains("old/output"));
    }

    // silent lossy-fallback in SaveJsBindingsOnly removed.
    // When the read or splice fails on an EXISTING file, the call must
    // throw rather than overwrite with a full serialization that strips
    // comments and unknown fields.
    [TestMethod]
    public void SaveJsBindingsOnly_ExistingFileLocked_ThrowsRatherThanLossyOverwrite()
    {
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        var originalYaml =
            "# top-level user comment that must survive\n"
            + "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "customField: 42  # unknown YAML field\n";
        File.WriteAllText(configPath, originalYaml);

        var cfg = _configService.Load();
        cfg.JsBindings = new JsBindingsConfig { Output = "bindings/winrt", Lang = "js" };

        // Hold an exclusive write lock that blocks File.ReadAllText.
        using var blocker = new FileStream(configPath, FileMode.Open, FileAccess.Write, FileShare.None);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => _configService.SaveJsBindingsOnly(cfg));
        StringAssert.Contains(ex.Message, "preserving comments",
            "Error must explain the lossy-write guard rather than silently corrupting the file.");
        StringAssert.Contains(ex.Message, "winapp.yaml",
            "Error must include the affected file path.");

        // Release the lock and verify the file on disk is the original
        // (not a lossy full-serialization).
        blocker.Dispose();
        var afterFailure = File.ReadAllText(configPath);
        Assert.AreEqual(originalYaml, afterFailure,
            "On failure, the file on disk must remain bit-identical to the original — "
            + "no comments stripped, no unknown fields dropped.");
    }

    [TestMethod]
    public void SaveJsBindingsOnly_NewFile_WritesViaStringify()
    {
        // Regression guard: the new-file (ConfigPath.Exists == false) path
        // still uses full serialization. There is nothing to preserve, so
        // Stringify is the right behavior.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsFalse(File.Exists(configPath), "Pre-condition: no existing config.");

        var cfg = new WinappConfig
        {
            Packages = { new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.8.39" } },
            JsBindings = new JsBindingsConfig { Output = "bindings/winrt", Lang = "js" },
        };

        _configService.SaveJsBindingsOnly(cfg);

        Assert.IsTrue(File.Exists(configPath), "File must be created.");
        var content = File.ReadAllText(configPath);
        StringAssert.Contains(content, "jsBindings:");
        StringAssert.Contains(content, "output: bindings/winrt");
    }
}
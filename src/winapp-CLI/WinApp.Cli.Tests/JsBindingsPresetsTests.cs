// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class JsBindingsPresetsTests
{
    private static readonly string[] _arr00 = ["Microsoft.WindowsAppSDK.AI", "Some.Vendor.Pkg"];
    private static readonly string[] _arr01 = ["Microsoft.WindowsAppSDK.AI", "Microsoft.WindowsAppSDK.WinUI"];
    private static readonly string[] _arr02 = ["Microsoft.WindowsAppSDK.AI"];
    private static readonly string[] _arr03 = ["bogus", "ai", ""];
    private static readonly string[] _arr04 = ["ai", "AI", "ai"];
    private static readonly string[] _arr05 = ["ai"];

    [TestMethod]
    public void KnownPresets_AiPreset_MapsToAiPackage()
    {
        Assert.IsTrue(JsBindingsPresets.TryResolve("ai", out var packages));
        CollectionAssert.AreEqual(
            _arr02,
            packages.ToList(),
            "AI preset must map to the Microsoft.WindowsAppSDK.AI NuGet package");
    }

    [TestMethod]
    public void KnownPresets_OnlyShipsAi()
    {
        // Only 'ai' ships today; trip this test when a new one lands.
        CollectionAssert.AreEqual(
            _arr05,
            JsBindingsPresets.KnownPresets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            "Unexpected preset registered. If intentional, update this test and docs/js-bindings.md.");
    }

    [TestMethod]
    public void TryResolve_IsCaseInsensitive()
    {
        Assert.IsTrue(JsBindingsPresets.TryResolve("AI", out var ai));
        Assert.AreEqual(1, ai.Count);
        Assert.IsTrue(JsBindingsPresets.TryResolve("Ai", out var aiMixed));
        Assert.AreEqual(1, aiMixed.Count);
    }

    [TestMethod]
    public void TryResolve_UnknownPreset_ReturnsFalseAndEmpty()
    {
        Assert.IsFalse(JsBindingsPresets.TryResolve("unknown-xyz", out var packages));
        Assert.AreEqual(0, packages.Count);
    }

    [TestMethod]
    public void KnownPresetsDisplay_IsAlphabeticallyOrdered()
    {
        var display = JsBindingsPresets.KnownPresetsDisplay();
        Assert.AreEqual("ai", display,
            "Display must be alphabetical so help output is stable");
    }

    // ResolveAndUnion — multi-preset union (only 'ai' ships today).

    [TestMethod]
    public void ResolveAndUnion_EmptyInput_ReturnsEmpty()
    {
        var result = JsBindingsPresets.ResolveAndUnion(Array.Empty<string>());
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ResolveAndUnion_SinglePreset_EquivalentToTryResolve()
    {
        var result = JsBindingsPresets.ResolveAndUnion(_arr05);
        CollectionAssert.AreEqual(
            _arr02,
            result.ToList(),
            "Single-preset union must produce the preset's package IDs in declaration order");
    }

    [TestMethod]
    public void ResolveAndUnion_DuplicatePresets_DedupesPackageIds()
    {
        // ai listed multiple times (mixed case to also exercise the
        // case-insensitive comparer) — must collapse to one set.
        var result = JsBindingsPresets.ResolveAndUnion(_arr04);
        CollectionAssert.AreEqual(
            _arr02,
            result.ToList(),
            "Repeated presets must collapse — no double package IDs");
    }

    [TestMethod]
    public void ResolveAndUnion_UnknownNames_AreSkippedSilently()
    {
        // Skip unknown but keep the known one. Validation is the CLI parser's job.
        var result = JsBindingsPresets.ResolveAndUnion(_arr03);
        CollectionAssert.AreEqual(
            _arr02,
            result.ToList());
    }

    [TestMethod]
    public void AliasFlagName_FormatsAsExpected()
    {
        // Single source of truth for the --js-bindings-{preset} naming scheme.
        Assert.AreEqual("--js-bindings-ai", JsBindingsPresets.AliasFlagName("ai"));
        Assert.AreEqual("--js-bindings-ai", JsBindingsPresets.AliasFlagName("AI"),
            "Casing of input must not leak into the flag name");
        Assert.AreEqual("--js-bindings-future", JsBindingsPresets.AliasFlagName("Future"),
            "Format must work for any preset name (regression guard for future presets)");
    }

    // Per-package winmd categorization — emit / ref-only / skip.

    [TestMethod]
    public void ClassifyPackage_WinUI_IsSkipped()
    {
        // Pure XAML composables — drop entirely.
        Assert.AreEqual(WinmdPackageCategory.Skip,
            JsBindingsPresets.ClassifyPackage("Microsoft.WindowsAppSDK.WinUI"));
        Assert.AreEqual(WinmdPackageCategory.Skip,
            JsBindingsPresets.ClassifyPackage("microsoft.windowsappsdk.winui"),
            "Package ID match must be case-insensitive (NuGet cache lowercases).");
    }

    [TestMethod]
    public void ClassifyPackage_InteractiveExperiences_IsRefOnly()
    {
        // Ships Microsoft.UI.WindowId / Microsoft.Graphics.PointInt32 etc.
        // — primitives referenced by other packages.
        Assert.AreEqual(WinmdPackageCategory.RefOnly,
            JsBindingsPresets.ClassifyPackage("Microsoft.WindowsAppSDK.InteractiveExperiences"));
        Assert.AreEqual(WinmdPackageCategory.RefOnly,
            JsBindingsPresets.ClassifyPackage("microsoft.windowsappsdk.interactiveexperiences"));
    }

    [TestMethod]
    public void ClassifyPackage_UnknownPackage_DefaultsToEmit()
    {
        Assert.AreEqual(WinmdPackageCategory.Emit,
            JsBindingsPresets.ClassifyPackage("Microsoft.WindowsAppSDK.AI"));
        Assert.AreEqual(WinmdPackageCategory.Emit,
            JsBindingsPresets.ClassifyPackage("Microsoft.WindowsAppSDK.Foundation"));
        Assert.AreEqual(WinmdPackageCategory.Emit,
            JsBindingsPresets.ClassifyPackage("anything.else"));
        Assert.AreEqual(WinmdPackageCategory.Emit,
            JsBindingsPresets.ClassifyPackage(""),
            "Empty/null package id (e.g. vendor winmds outside the cache) defaults to Emit.");
    }

    [TestMethod]
    public void ExtractPackageIdFromPath_FlatMetadataLayout_ReturnsPackageId()
    {
        // The Microsoft.WindowsAppSDK.AI layout: metadata files sit directly under metadata/.
        var p = @"C:\Users\u\.nuget\packages\microsoft.windowsappsdk.ai\1.8.39\metadata\Microsoft.Windows.AI.winmd";
        Assert.AreEqual("microsoft.windowsappsdk.ai", JsBindingsPresets.ExtractPackageIdFromPath(p));
    }

    [TestMethod]
    public void ExtractPackageIdFromPath_NestedSdkVersionLayout_ReturnsPackageId()
    {
        // The InteractiveExperiences layout: metadata files nested under metadata/10.0.18362.0/.
        var p = @"C:\Users\u\.nuget\packages\microsoft.windowsappsdk.interactiveexperiences\1.8.251104001\metadata\10.0.18362.0\Microsoft.UI.winmd";
        Assert.AreEqual("microsoft.windowsappsdk.interactiveexperiences",
            JsBindingsPresets.ExtractPackageIdFromPath(p));
    }

    [TestMethod]
    public void ExtractPackageIdFromPath_NonNuGetPath_ReturnsNull()
    {
        // Vendor winmd outside the cache — no "packages" segment.
        var p = @"C:\src\my-project\vendor\Custom.winmd";
        Assert.IsNull(JsBindingsPresets.ExtractPackageIdFromPath(p));
    }

    [TestMethod]
    public void ExtractPackageIdFromPath_ForwardSlashPath_AlsoWorks()
    {
        // Defensive: we may get either separator on Windows.
        var p = "C:/Users/u/.nuget/packages/microsoft.windowsappsdk.ai/1.8.39/metadata/Foo.winmd";
        Assert.AreEqual("microsoft.windowsappsdk.ai", JsBindingsPresets.ExtractPackageIdFromPath(p));
    }

    [TestMethod]
    public void PartitionByPackageCategory_MixedSet_SplitsCorrectly()
    {
        var files = new[]
        {
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.ai\1.8.39\metadata\Microsoft.Windows.AI.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.foundation\1.8.0\metadata\Microsoft.Windows.Storage.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.winui\1.8.0\metadata\Microsoft.UI.Xaml.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.interactiveexperiences\1.8.0\metadata\10.0.18362.0\Microsoft.UI.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.interactiveexperiences\1.8.0\metadata\10.0.18362.0\Microsoft.Graphics.winmd"),
            new FileInfo(@"C:\src\vendor\MyCo.Custom.winmd"),
        };

        var p = JsBindingsPresets.PartitionByPackageCategory(files);

        Assert.AreEqual(3, p.Emit.Count,
            "AI + Foundation + vendor winmd should land in Emit");
        Assert.IsTrue(p.Emit.Any(f => f.FullName.EndsWith("Microsoft.Windows.AI.winmd", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(p.Emit.Any(f => f.FullName.EndsWith("Microsoft.Windows.Storage.winmd", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(p.Emit.Any(f => f.FullName.EndsWith("MyCo.Custom.winmd", StringComparison.OrdinalIgnoreCase)),
            "Vendor winmds outside the NuGet cache must default to Emit.");

        Assert.AreEqual(2, p.RefOnly.Count,
            "Both InteractiveExperiences winmds (Microsoft.UI + Microsoft.Graphics) go to RefOnly");

        Assert.AreEqual(1, p.Skipped.Count,
            "Microsoft.UI.Xaml.winmd from the WinUI package is dropped entirely");
        Assert.IsTrue(p.Skipped[0].FullName.EndsWith("Microsoft.UI.Xaml.winmd", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PartitionByPackageCategory_EmptyInput_ReturnsAllEmpty()
    {
        var p = JsBindingsPresets.PartitionByPackageCategory(Array.Empty<FileInfo>());
        Assert.AreEqual(0, p.Emit.Count);
        Assert.AreEqual(0, p.RefOnly.Count);
        Assert.AreEqual(0, p.Skipped.Count);
    }

    // -------------------------------------------------------------------------
    // v2.3 — PackageCategoryOverrides
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ClassifyPackage_UserEmit_OverridesDefaultSkip()
    {
        // Default would Skip WinUI; user force-emits → must become Emit.
        var ov = new JsBindingsPresets.PackageCategoryOverrides
        {
            Emit = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.WindowsAppSDK.WinUI" },
        };
        Assert.AreEqual(WinmdPackageCategory.Emit,
            JsBindingsPresets.ClassifyPackage("Microsoft.WindowsAppSDK.WinUI", ov));
    }

    [TestMethod]
    public void ClassifyPackage_UserSkip_AppendedToDefault()
    {
        var ov = new JsBindingsPresets.PackageCategoryOverrides
        {
            Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Some.New.XAML.Package" },
        };
        Assert.AreEqual(WinmdPackageCategory.Skip,
            JsBindingsPresets.ClassifyPackage("Some.New.XAML.Package", ov));
        // Default skip list still honored.
        Assert.AreEqual(WinmdPackageCategory.Skip,
            JsBindingsPresets.ClassifyPackage("Microsoft.WindowsAppSDK.WinUI", ov));
    }

    [TestMethod]
    public void ClassifyPackage_UserRefOnly_AppendedToDefault()
    {
        var ov = new JsBindingsPresets.PackageCategoryOverrides
        {
            RefOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendor.PrimitiveTypes" },
        };
        Assert.AreEqual(WinmdPackageCategory.RefOnly,
            JsBindingsPresets.ClassifyPackage("Vendor.PrimitiveTypes", ov));
        // InteractiveExperiences still ref-only by default.
        Assert.AreEqual(WinmdPackageCategory.RefOnly,
            JsBindingsPresets.ClassifyPackage("Microsoft.WindowsAppSDK.InteractiveExperiences", ov));
    }

    [TestMethod]
    public void ClassifyPackage_UserEmit_BeatsBothUserSkipAndUserRefOnly()
    {
        // If users list the same package in both Skip and Emit, Emit wins
        // (most permissive — they explicitly want bindings).
        var ov = new JsBindingsPresets.PackageCategoryOverrides
        {
            Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Foo" },
            RefOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Foo" },
            Emit = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Foo" },
        };
        Assert.AreEqual(WinmdPackageCategory.Emit,
            JsBindingsPresets.ClassifyPackage("Foo", ov));
    }

    [TestMethod]
    public void PackageCategoryOverrides_From_NullConfig_ReturnsEmpty()
    {
        var ov = JsBindingsPresets.PackageCategoryOverrides.From(null);
        Assert.IsNull(ov.Skip);
        Assert.IsNull(ov.RefOnly);
        Assert.IsNull(ov.Emit);
    }

    [TestMethod]
    public void PackageCategoryOverrides_From_PopulatedConfig_MapsAllThreeLists()
    {
        var cfg = new WinApp.Cli.Models.JsBindingsConfig
        {
            SkipPackages = { "S1" },
            RefOnlyPackages = { "R1", "R2" },
            EmitPackages = { "E1" },
        };
        var ov = JsBindingsPresets.PackageCategoryOverrides.From(cfg);
        Assert.IsNotNull(ov.Skip);
        Assert.IsTrue(ov.Skip!.Contains("S1"));
        Assert.IsNotNull(ov.RefOnly);
        Assert.AreEqual(2, ov.RefOnly!.Count);
        Assert.IsNotNull(ov.Emit);
        Assert.IsTrue(ov.Emit!.Contains("E1"));
    }

    [TestMethod]
    public void PartitionByPackageCategory_UserOverrides_RedirectPackage()
    {
        // WinUI default = skip; force-emit it via override → ends up in Emit.
        var files = new[]
        {
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.ai\1.8.39\metadata\AI.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.winui\1.8\metadata\Xaml.winmd"),
        };
        var ov = new JsBindingsPresets.PackageCategoryOverrides
        {
            Emit = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.WindowsAppSDK.WinUI" },
        };
        var p = JsBindingsPresets.PartitionByPackageCategory(files, ov);
        Assert.AreEqual(2, p.Emit.Count, "Both AI and WinUI should now emit (user force-emit on WinUI).");
        Assert.AreEqual(0, p.Skipped.Count);
    }

    // emit scope demotes out-of-scope emit-category packages
    // to RefOnly so codegen still has metadata for cross-package type
    // resolution. This is the core regression test for the live-discovery
    // bug where scope was applied BEFORE discovery, dropping refs entirely.
    [TestMethod]
    public void PartitionByPackageCategory_EmitScope_OutOfScopeEmitPackages_DemotedToRefOnly()
    {
        var files = new[]
        {
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.ai\1.8.39\metadata\AI.winmd"),
            // Core WindowsAppSDK is NOT in scope but its types are
            // referenced by AI — must end up as RefOnly, not dropped.
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk\1.8.39\lib\Core.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\microsoft.web.webview2\1.0.0\runtimes\WebView2.winmd"),
        };

        var scope = _arr02;
        var p = JsBindingsPresets.PartitionByPackageCategory(
            files, overrides: null, nugetCacheRoot: null, emitScope: scope);

        Assert.AreEqual(1, p.Emit.Count, "Only AI is in scope, so only AI emits.");
        Assert.IsTrue(p.Emit[0].FullName.EndsWith("AI.winmd", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(2, p.RefOnly.Count,
            "Out-of-scope emit-category packages (core SDK + WebView2) MUST be demoted to RefOnly, "
            + "NOT dropped — codegen needs them for type resolution.");
        Assert.IsTrue(p.RefOnly.Any(f => f.FullName.EndsWith("Core.winmd", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(p.RefOnly.Any(f => f.FullName.EndsWith("WebView2.winmd", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PartitionByPackageCategory_EmitScope_NullOrEmpty_NoFiltering()
    {
        // No emit scope = full default partitioning (no demotion happens).
        var files = new[]
        {
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.ai\1.8.39\metadata\AI.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk\1.8.39\lib\Core.winmd"),
        };

        var pNull = JsBindingsPresets.PartitionByPackageCategory(files, emitScope: null);
        Assert.AreEqual(2, pNull.Emit.Count, "Null scope = full emit.");
        Assert.AreEqual(0, pNull.RefOnly.Count);

        var pEmpty = JsBindingsPresets.PartitionByPackageCategory(files, emitScope: Array.Empty<string>());
        Assert.AreEqual(2, pEmpty.Emit.Count, "Empty scope = full emit (same as null).");
    }

    [TestMethod]
    public void PartitionByPackageCategory_EmitScope_SkipCategoryWins()
    {
        // Skip-classified packages stay Skipped even when in scope — the
        // user-override skip is stronger than scope inclusion.
        var files = new[]
        {
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.ai\1.8.39\metadata\AI.winmd"),
            // WinUI is in the default-skip set.
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.winui\1.8\metadata\Xaml.winmd"),
        };

        var scope = _arr01;
        var p = JsBindingsPresets.PartitionByPackageCategory(
            files, overrides: null, nugetCacheRoot: null, emitScope: scope);

        Assert.AreEqual(1, p.Emit.Count, "AI emits.");
        Assert.AreEqual(1, p.Skipped.Count, "WinUI stays skipped even though in scope.");
    }

    [TestMethod]
    public void PartitionByPackageCategory_EmitScope_RefOnlyCategoryWins()
    {
        // RefOnly-classified packages stay RefOnly when in scope — the
        // classification is stronger.
        var files = new[]
        {
            new FileInfo(@"C:\u\.nuget\packages\microsoft.windowsappsdk.ai\1.8.39\metadata\AI.winmd"),
            new FileInfo(@"C:\u\.nuget\packages\some.vendor.pkg\1.0\lib\Vendor.winmd"),
        };
        var ov = new JsBindingsPresets.PackageCategoryOverrides
        {
            RefOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Some.Vendor.Pkg" },
        };

        var p = JsBindingsPresets.PartitionByPackageCategory(
            files, ov, nugetCacheRoot: null,
            emitScope: _arr00);

        Assert.AreEqual(1, p.Emit.Count, "AI emits.");
        Assert.AreEqual(1, p.RefOnly.Count, "Vendor stays RefOnly via classification override.");
    }
}

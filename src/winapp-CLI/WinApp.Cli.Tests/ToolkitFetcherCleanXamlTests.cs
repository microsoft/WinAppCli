// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ToolkitFetcher.CleanXaml"/> and
/// <see cref="ToolkitFetcher.DetectXmlnsImports"/> — the demo-cleanup + namespace
/// detection that must never emit a snippet that fails XAML parsing (missing xmlns,
/// a dropped-but-referenced resource, or a dropped-but-bound element).
/// </summary>
[TestClass]
public class ToolkitFetcherCleanXamlTests
{
    // --- Finding 1: muxc namespace must be advertised when the snippet uses it -----

    [TestMethod]
    public void DetectXmlnsImports_EmitsMuxc_WhenSnippetUsesMuxcPrefix()
    {
        var xaml = "<controls:WrapLayout>\n  <muxc:ItemsRepeater />\n</controls:WrapLayout>";
        var imports = ToolkitFetcher.DetectXmlnsImports(xaml);
        Assert.IsTrue(imports.Any(i => i.Contains("xmlns:muxc=\"using:Microsoft.UI.Xaml.Controls\"")),
            "a snippet using muxc: must advertise the muxc namespace or it won't parse");
    }

    [TestMethod]
    public void DetectXmlnsImports_OmitsMuxc_WhenUnused()
    {
        var xaml = "<controls:DockPanel>\n  <TextBox />\n</controls:DockPanel>";
        var imports = ToolkitFetcher.DetectXmlnsImports(xaml);
        Assert.IsFalse(imports.Any(i => i.Contains("xmlns:muxc")),
            "don't advertise muxc when the snippet doesn't use it");
        Assert.IsTrue(imports.Any(i => i.Contains("xmlns:controls")), "controls is always required");
    }

    // --- Finding 2: a referenced resource definition must survive cleanup ----------

    [TestMethod]
    public void CleanXaml_KeepsResourceBlock_WhenKeyStillReferenced()
    {
        var xaml =
            "<controls:WrapLayout>\n" +
            "  <controls:WrapLayout.Resources>\n" +
            "    <DataTemplate x:Key=\"WrapTemplate\"><TextBlock Text=\"{x:Bind}\" /></DataTemplate>\n" +
            "  </controls:WrapLayout.Resources>\n" +
            "  <muxc:ItemsRepeater ItemTemplate=\"{StaticResource WrapTemplate}\" />\n" +
            "</controls:WrapLayout>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        StringAssert.Contains(cleaned, "x:Key=\"WrapTemplate\"",
            "a DataTemplate referenced by {StaticResource} must not be dropped");
        StringAssert.Contains(cleaned, "{StaticResource WrapTemplate}", "the reference itself survives");
    }

    [TestMethod]
    public void CleanXaml_DropsResourceBlock_WhenNothingReferencesIt()
    {
        var xaml =
            "<controls:DockPanel>\n" +
            "  <controls:DockPanel.Resources>\n" +
            "    <DataTemplate x:Key=\"UnusedTemplate\"><TextBlock /></DataTemplate>\n" +
            "  </controls:DockPanel.Resources>\n" +
            "  <TextBox />\n" +
            "</controls:DockPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("UnusedTemplate", StringComparison.Ordinal),
            "an unreferenced demo resource block is still cleaned away");
    }

    [TestMethod]
    public void CleanXaml_DropsResourceBlock_WithNoKeyedResources()
    {
        // Implicit-style-only Resources (no x:Key) never dangle a reference → safe to drop.
        var xaml =
            "<controls:DockPanel>\n" +
            "  <controls:DockPanel.Resources>\n" +
            "    <Style TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"14\" /></Style>\n" +
            "  </controls:DockPanel.Resources>\n" +
            "  <TextBox />\n" +
            "</controls:DockPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("<controls:DockPanel.Resources>", StringComparison.Ordinal),
            "an implicit-style-only Resources block is safe to drop");
    }

    // --- Finding 3: a Border bound to by name must survive cleanup ------------------

    [TestMethod]
    public void CleanXaml_KeepsNamedBorder_WhenReferencedByBinding()
    {
        var xaml =
            "<controls:ContentSizer TargetControl=\"{x:Bind SomeContent}\">\n" +
            "  <Border x:Name=\"SomeContent\" Background=\"Gray\" />\n" +
            "</controls:ContentSizer>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        StringAssert.Contains(cleaned, "x:Name=\"SomeContent\"",
            "a Border referenced by {x:Bind} must keep its name");
        StringAssert.Contains(cleaned, "<Border", "the referenced Border element must not be removed");
        StringAssert.Contains(cleaned, "{x:Bind SomeContent}", "the binding survives and now resolves");
    }

    [TestMethod]
    public void CleanXaml_DropsUnnamedEmptyBorderPlaceholder()
    {
        var xaml =
            "<controls:DockPanel>\n" +
            "  <Border Background=\"Gray\" />\n" +
            "  <TextBox />\n" +
            "</controls:DockPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("<Border", StringComparison.Ordinal),
            "an unnamed empty Border placeholder is still cleaned away");
    }

    [TestMethod]
    public void CleanXaml_StripsUnreferencedNames_ButKeepsReferencedOnes()
    {
        var xaml =
            "<StackPanel>\n" +
            "  <TextBox x:Name=\"NoiseName\" />\n" +
            "  <controls:ContentSizer TargetControl=\"{x:Bind Keep}\" />\n" +
            "  <Border x:Name=\"Keep\" Background=\"Gray\" />\n" +
            "</StackPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("NoiseName", StringComparison.Ordinal),
            "an unreferenced x:Name is still stripped as demo noise");
        StringAssert.Contains(cleaned, "x:Name=\"Keep\"", "a referenced x:Name is preserved");
    }
}

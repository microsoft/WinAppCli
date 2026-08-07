// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="GalleryFetcher.CleanGalleryContent"/> — the demo-cleanup
/// pass. The invariant that matters to a user: the emitted XAML must not reference an
/// event handler the accompanying C# never defines, because the Gallery's code-behind
/// extractor does not surface <c>*_Loaded</c> methods and an agent pastes the snippet
/// verbatim. A dangling <c>Loaded="X_Loaded"</c> is a compile break in the user's project.
/// </summary>
[TestClass]
public class GalleryFetcherCleanContentTests
{
    [TestMethod]
    public void CleanGalleryContent_InlineLoadedHandler_IsRemoved()
    {
        // The real gallery-tabview-1 shape: the demo handler sits INLINE, on a line that
        // also carries '<' and '>'. The line-level filter deliberately keeps such lines,
        // so the attribute has to be stripped before it runs.
        var xaml = "<TabView AddTabButtonClick=\"TabView_AddButtonClick\" "
                 + "TabCloseRequested=\"TabView_TabCloseRequested\" Loaded=\"TabView_Loaded\" />";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        Assert.IsFalse(cleaned.Contains("TabView_Loaded"), "dangling Loaded handler must be stripped");
        StringAssert.Contains(cleaned, "TabView_AddButtonClick", "real handlers must survive");
        StringAssert.Contains(cleaned, "TabView_TabCloseRequested", "real handlers must survive");
        StringAssert.Contains(cleaned, "/>", "the tag must stay well-formed");
    }

    [TestMethod]
    public void CleanGalleryContent_OwnLineLoadedHandler_IsRemovedWithoutBreakingTheTag()
    {
        var xaml = "<TabView x:Name=\"Tabs\"\n"
                 + "         Loaded=\"TabView_Loaded\"\n"
                 + "         TabWidthMode=\"Equal\">\n"
                 + "</TabView>";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        Assert.IsFalse(cleaned.Contains("TabView_Loaded"), "dangling Loaded handler must be stripped");
        StringAssert.Contains(cleaned, "TabWidthMode=\"Equal\"", "sibling attributes must survive");
        Assert.IsTrue(
            ScenarioSanitizer.XamlIsWellFormed(cleaned),
            $"cleanup must leave well-formed XAML. Got:\n{cleaned}");
    }

    [TestMethod]
    public void CleanGalleryContent_NonDemoLoadedHandler_IsKept()
    {
        // Only the "*_Loaded" demo convention is stripped. A handler named anything else
        // is the sample's own logic and the code-behind extractor does surface it.
        var xaml = "<Grid Loaded=\"OnGridReady\" />";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        StringAssert.Contains(cleaned, "OnGridReady", "non-demo handlers must not be stripped");
    }

    [TestMethod]
    public void CleanGalleryContent_DemoLayoutAttributesOnOwnLine_StillRemoved()
    {
        // Guards the pre-existing line-level filter against regression from the new
        // attribute-level pass running ahead of it.
        var xaml = "<Button\n"
                 + "    Width=\"300\"\n"
                 + "    Margin=\"-8\"\n"
                 + "    Content=\"Go\">\n"
                 + "</Button>";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        Assert.IsFalse(cleaned.Contains("Width=\"300\""), "own-line demo width must still be dropped");
        Assert.IsFalse(cleaned.Contains("Margin=\"-8\""), "own-line negative margin must still be dropped");
        StringAssert.Contains(cleaned, "Content=\"Go\"");
    }
}

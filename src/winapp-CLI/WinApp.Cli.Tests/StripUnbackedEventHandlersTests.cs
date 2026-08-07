// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ControlSnippetText.StripUnbackedEventHandlers"/> — the
/// corpus-boundary mitigation that removes event handlers a snippet's emitted C#
/// doesn't define (the WinUI Gallery keeps handlers in shared page code-behind we
/// don't fetch, so ~44 scenarios shipped XAML wired to a missing handler). The
/// invariant: a pasted snippet never references a handler that isn't there, while
/// handlers that ARE backed (e.g. TabView's) and command bindings survive.
/// </summary>
[TestClass]
public class StripUnbackedEventHandlersTests
{
    [TestMethod]
    public void NoCsharp_StripsEveryBareMethodHandler()
    {
        var xaml = "<AppBarButton Icon=\"Like\" Label=\"SymbolIcon\" Click=\"AppBarButton_Click\"/>";
        var result = ControlSnippetText.StripUnbackedEventHandlers(xaml, null);

        Assert.IsFalse(result.Contains("Click=", StringComparison.Ordinal),
            "with no code-behind, the dangling handler must be stripped");
        StringAssert.Contains(result, "Icon=\"Like\"", "non-event attributes are preserved");
        StringAssert.Contains(result, "Label=\"SymbolIcon\"", "non-event attributes are preserved");
    }

    [TestMethod]
    public void CsharpMissingMethod_StripsOnlyTheUnbackedHandler()
    {
        // Two handlers; C# defines only one → strip the other, keep the defined one.
        var xaml = "<StackPanel>" +
                   "<Button Click=\"Save_Click\"/>" +
                   "<Button Click=\"MaximizeBtn_Click\"/>" +
                   "</StackPanel>";
        var csharp = "private void Save_Click(object sender, RoutedEventArgs e) { Save(); }";

        var result = ControlSnippetText.StripUnbackedEventHandlers(xaml, csharp);

        StringAssert.Contains(result, "Click=\"Save_Click\"", "a handler defined in C# must be kept");
        Assert.IsFalse(result.Contains("MaximizeBtn_Click", StringComparison.Ordinal),
            "a handler NOT defined in C# must be stripped");
    }

    [TestMethod]
    public void BackedHandlers_AreKept()
    {
        // The TabView case (not in the flagged set): both handlers are defined.
        var xaml = "<TabView AddTabButtonClick=\"TabView_AddButtonClick\" " +
                   "TabCloseRequested=\"TabView_TabCloseRequested\" />";
        var csharp =
            "private void TabView_AddButtonClick(TabView sender, object args) { }\n" +
            "private void TabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) { }";

        var result = ControlSnippetText.StripUnbackedEventHandlers(xaml, csharp);

        Assert.AreEqual(xaml, result, "handlers backed by the emitted C# must all be preserved unchanged");
    }

    [TestMethod]
    public void CommandBindings_AreNeverStripped()
    {
        var xaml = "<Button Click=\"{x:Bind SaveCommand}\" Content=\"Save\"/>";
        var result = ControlSnippetText.StripUnbackedEventHandlers(xaml, null);

        StringAssert.Contains(result, "Click=\"{x:Bind SaveCommand}\"",
            "a command binding is not a bare-method handler and must be kept even with no C#");
    }

    [TestMethod]
    public void TypedEvents_AreRecognizedAndStripped()
    {
        // SwipeControl Invoked / AutoSuggestBox QuerySubmitted / GridView ItemClick /
        // ComboBox SelectionChanged — the typed events that also appear unbacked.
        var xaml = "<StackPanel>" +
                   "<SwipeItem Invoked=\"Accept_ItemInvoked\"/>" +
                   "<AutoSuggestBox QuerySubmitted=\"Box_QuerySubmitted\"/>" +
                   "<GridView ItemClick=\"Grid_ItemClick\"/>" +
                   "<ComboBox SelectionChanged=\"Combo_SelectionChanged\"/>" +
                   "</StackPanel>";

        var result = ControlSnippetText.StripUnbackedEventHandlers(xaml, null);

        foreach (var attr in new[] { "Invoked=", "QuerySubmitted=", "ItemClick=", "SelectionChanged=" })
        {
            Assert.IsFalse(result.Contains(attr, StringComparison.Ordinal),
                $"unbacked typed handler {attr} must be stripped");
        }
    }

    [TestMethod]
    public void ItemInvoked_IsNotMisreadAsInvoked()
    {
        // Regex alternation must not clip ItemInvoked to Invoked.
        var xaml = "<NavigationView ItemInvoked=\"Nav_ItemInvoked\"/>";
        var result = ControlSnippetText.StripUnbackedEventHandlers(xaml, null);

        Assert.IsFalse(result.Contains("ItemInvoked", StringComparison.Ordinal),
            "ItemInvoked must be recognized and stripped as a whole");
        Assert.IsFalse(result.Contains("Invoked", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CallingButNotDefiningTheMethod_StillStrips()
    {
        // C# that references the handler name only in a call (not a declaration) does
        // not count as backing — must still strip so the snippet compiles.
        var xaml = "<Button Click=\"Do_Click\"/>";
        var csharp = "public void Init() { Do_Click(this, null); }"; // calls, never declares

        var result = ControlSnippetText.StripUnbackedEventHandlers(xaml, csharp);

        Assert.IsFalse(result.Contains("Click=", StringComparison.Ordinal),
            "a mere call to the method does not back the handler; strip it");
    }

    [TestMethod]
    public void EmptyOrNullXaml_ReturnedUnchanged()
    {
        Assert.AreEqual("", ControlSnippetText.StripUnbackedEventHandlers("", "cs"));
        Assert.IsNull(ControlSnippetText.StripUnbackedEventHandlers(null!, "cs"));
    }
}

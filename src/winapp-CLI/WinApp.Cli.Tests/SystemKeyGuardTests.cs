// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class SystemKeyGuardTests
{
    [TestMethod]
    [DataRow("win+l")]
    [DataRow("win+r")]
    [DataRow("win+d")]
    [DataRow("win+e")]
    [DataRow("cmd+l")]    // alias for win
    [DataRow("super+tab")]
    public void WinModifiedCombos_AreReportedGenerically(string keys)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        CollectionAssert.AreEqual(new[] { "win+<key>" }, combos.ToArray());
    }

    [TestMethod]
    [DataRow("ctrl+shift+esc", "ctrl+shift+esc")]
    [DataRow("ctrl+alt+del", "ctrl+alt+del")]
    [DataRow("ctrl+esc", "ctrl+esc")]
    [DataRow("alt+tab", "alt+tab")]
    [DataRow("alt+esc", "alt+esc")]
    [DataRow("alt+f4", "alt+f4")]
    public void KnownSystemCombos_AreReportedByName(string keys, string expected)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        CollectionAssert.AreEqual(new[] { expected }, combos.ToArray());
    }

    [TestMethod]
    public void LoneWinKey_IsReported()
    {
        // The Win key has no named token; it can only be sent raw via vk=0x5B.
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("vk=0x5B"));
        CollectionAssert.AreEqual(new[] { "win" }, combos.ToArray());
    }

    [TestMethod]
    public void LonePrintScreen_IsReported()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("printscreen"));
        CollectionAssert.AreEqual(new[] { "printscreen" }, combos.ToArray());
    }

    [TestMethod]
    [DataRow("ctrl+a")]
    [DataRow("ctrl+c")]
    [DataRow("ctrl+shift+t")]
    [DataRow("enter")]
    [DataRow("down down up")]
    [DataRow("f5")]
    [DataRow("alt+a")]
    [DataRow("ctrl+alt+a")]
    public void OrdinaryCombos_AreNotReported(string keys)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(0, combos.Count);
    }

    [TestMethod]
    public void LiteralText_IsNotReported()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("Hello world"));
        Assert.AreEqual(0, combos.Count);
    }

    [TestMethod]
    public void Duplicates_AreCollapsed()
    {
        // Two Win combos collapse to a single generic entry.
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("win+l win+r"));
        CollectionAssert.AreEqual(new[] { "win+<key>" }, combos.ToArray());
    }

    [TestMethod]
    public void MultipleDistinctCombos_PreserveFirstSeenOrder()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("alt+f4 ctrl+esc alt+tab"));
        CollectionAssert.AreEqual(new[] { "alt+f4", "ctrl+esc", "alt+tab" }, combos.ToArray());
    }

    [TestMethod]
    public void MixedSafeAndSystemKeys_ReportsOnlySystem()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("ctrl+a win+l enter"));
        CollectionAssert.AreEqual(new[] { "win+<key>" }, combos.ToArray());
    }

    [TestMethod]
    public void EmptyActions_ReturnsEmpty()
    {
        Assert.AreEqual(0, SystemKeyGuard.FindSystemCombos([]).Count);
    }
}

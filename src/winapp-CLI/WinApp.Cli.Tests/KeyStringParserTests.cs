// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class KeyStringParserTests
{
    [TestMethod]
    public void Parse_NamedKey_ProducesSingleChord()
    {
        var actions = KeyStringParser.Parse("enter");
        Assert.AreEqual(1, actions.Count);
        var chord = (KeyChord)actions[0];
        Assert.AreEqual(0, chord.Modifiers.Count);
        Assert.AreEqual(0x0D, chord.Vk);   // VK_RETURN
        Assert.IsFalse(chord.Extended);
    }

    [TestMethod]
    public void Parse_ArrowKey_IsExtended()
    {
        var actions = KeyStringParser.Parse("down");
        var chord = (KeyChord)actions[0];
        Assert.AreEqual(0x28, chord.Vk);   // VK_DOWN
        Assert.IsTrue(chord.Extended);
    }

    [TestMethod]
    public void Parse_Sequence_ProducesOrderedChords()
    {
        var actions = KeyStringParser.Parse("down down enter");
        Assert.AreEqual(3, actions.Count);
        Assert.AreEqual(0x28, ((KeyChord)actions[0]).Vk);
        Assert.AreEqual(0x28, ((KeyChord)actions[1]).Vk);
        Assert.AreEqual(0x0D, ((KeyChord)actions[2]).Vk);
    }

    [TestMethod]
    public void Parse_ModifierCombo_CapturesModifiersAndMainKey()
    {
        var actions = KeyStringParser.Parse("ctrl+shift+a");
        Assert.AreEqual(1, actions.Count);
        var chord = (KeyChord)actions[0];
        CollectionAssert.AreEqual(new ushort[] { 0x11, 0x10 }, chord.Modifiers.ToArray()); // CONTROL, SHIFT
        Assert.AreEqual(0x41, chord.Vk); // 'A'
    }

    [TestMethod]
    public void Parse_AltCombo_MapsAltModifier()
    {
        var actions = KeyStringParser.Parse("alt+f4");
        var chord = (KeyChord)actions[0];
        CollectionAssert.AreEqual(new ushort[] { 0x12 }, chord.Modifiers.ToArray()); // MENU (ALT)
        Assert.AreEqual(0x73, chord.Vk); // VK_F4
    }

    [TestMethod]
    public void Parse_LiteralText_ProducesTextInput()
    {
        var actions = KeyStringParser.Parse("hello");
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("hello", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    [DataRow("vk=0x42", (ushort)0x42)]
    [DataRow("vk=66", (ushort)66)]
    [DataRow("VK=0X1B", (ushort)0x1B)]
    public void Parse_RawVirtualKey_ParsesValue(string token, ushort expected)
    {
        var actions = KeyStringParser.Parse(token);
        Assert.AreEqual(expected, ((KeyChord)actions[0]).Vk);
    }

    [TestMethod]
    public void Parse_MixedTokens_PreservesOrderAndTypes()
    {
        var actions = KeyStringParser.Parse("ctrl+a delete world");
        Assert.AreEqual(3, actions.Count);
        Assert.IsInstanceOfType<KeyChord>(actions[0]);
        Assert.AreEqual(0x2E, ((KeyChord)actions[1]).Vk); // VK_DELETE
        Assert.IsInstanceOfType<TextInput>(actions[2]);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Parse_Empty_Throws(string keys)
    {
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse(keys));
    }

    [TestMethod]
    [DataRow("vk=0xZZ")]
    [DataRow("vk=99999")]
    [DataRow("vk=0")]
    public void Parse_InvalidVirtualKey_Throws(string token)
    {
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse(token));
    }

    [TestMethod]
    public void Parse_UnknownModifierInCombo_Throws()
    {
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse("hyper+a"));
    }
}


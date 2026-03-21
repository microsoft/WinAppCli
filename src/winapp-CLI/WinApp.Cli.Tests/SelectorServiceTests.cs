// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class SelectorServiceTests
{
    private readonly SelectorService _sut = new();

    [TestMethod]
    public void Parse_ElementId_ReturnsElementId()
    {
        var result = _sut.Parse("e5");
        Assert.AreEqual("e5", result.ElementId);
        Assert.IsTrue(result.IsElementId);
        Assert.IsNull(result.Name);
        Assert.IsNull(result.Type);
    }

    [TestMethod]
    public void Parse_ElementIdZero_ReturnsElementId()
    {
        var result = _sut.Parse("e0");
        Assert.AreEqual("e0", result.ElementId);
    }

    [TestMethod]
    public void Parse_LargeElementId_ReturnsElementId()
    {
        var result = _sut.Parse("e12345");
        Assert.AreEqual("e12345", result.ElementId);
    }

    [TestMethod]
    public void Parse_NameSelector_ReturnsName()
    {
        var result = _sut.Parse("#Submit");
        Assert.AreEqual("Submit", result.Name);
        Assert.IsNull(result.ElementId);
        Assert.IsNull(result.Type);
    }

    [TestMethod]
    public void Parse_AutomationIdSelector_ReturnsAutomationId()
    {
        var result = _sut.Parse("$SearchBox");
        Assert.AreEqual("SearchBox", result.AutomationId);
        Assert.IsNull(result.Name);
        Assert.IsNull(result.Type);
    }

    [TestMethod]
    public void Parse_TypeSelector_ReturnsType()
    {
        var result = _sut.Parse("Button");
        Assert.AreEqual("Button", result.Type);
        Assert.IsNull(result.ElementId);
        Assert.IsNull(result.Name);
    }

    [TestMethod]
    public void Parse_TypePlusName_ReturnsBoth()
    {
        var result = _sut.Parse("Button#OK");
        Assert.AreEqual("Button", result.Type);
        Assert.AreEqual("OK", result.Name);
        Assert.IsNull(result.AutomationId);
    }

    [TestMethod]
    public void Parse_TypePlusAutomationId_ReturnsBoth()
    {
        var result = _sut.Parse("TextBox$SearchInput");
        Assert.AreEqual("TextBox", result.Type);
        Assert.AreEqual("SearchInput", result.AutomationId);
        Assert.IsNull(result.Name);
    }

    [TestMethod]
    public void Parse_NotElementId_Edit_ReturnsType()
    {
        // "Edit" starts with 'E' but isn't e+digits — should be type
        var result = _sut.Parse("Edit");
        Assert.AreEqual("Edit", result.Type);
        Assert.IsNull(result.ElementId);
    }

    [TestMethod]
    public void Parse_NotElementId_Element_ReturnsType()
    {
        // "Element" starts with 'e' but has non-digit chars
        var result = _sut.Parse("Element");
        Assert.AreEqual("Element", result.Type);
        Assert.IsNull(result.ElementId);
    }

    [TestMethod]
    public void Parse_Empty_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _sut.Parse(""));
    }

    [TestMethod]
    public void Parse_Whitespace_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _sut.Parse("   "));
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class XamlTriageServiceTests
{
    // Mirrors the real failure shape when OS symbols for combase.dll are unavailable:
    // the extension loads and detects the stowed exception but cannot expand its structs.
    private const string SymbolGapOutput =
        "************* Symbol Loading Error Summary **************\n" +
        "Module name            Error\n" +
        "combase                The system cannot find the file specified\n" +
        "JavaScript script successfully loaded from 'winui-dbgext.js'\n" +
        "*** WARNING: Symbols for combase.dll not loaded/unavailable.\n" +
        "Error: Error: Invalid argument to method 'createPointerObject' [__Initialize @winui-dbgext (line 3116 col 39)]";

    [TestMethod]
    public void DescribeSymbolGap_WithSymbols_ExplainsServer404()
    {
        var note = XamlTriageService.DescribeSymbolGap(SymbolGapOutput, useSymbols: true);

        Assert.IsNotNull(note);
        StringAssert.Contains(note, "0xC000027B");
        StringAssert.Contains(note, "combase.dll");
        StringAssert.Contains(note, "404");
    }

    [TestMethod]
    public void DescribeSymbolGap_WithoutSymbols_SuggestsSymbolsFlag()
    {
        var note = XamlTriageService.DescribeSymbolGap(SymbolGapOutput, useSymbols: false);

        Assert.IsNotNull(note);
        StringAssert.Contains(note, "--symbols");
    }

    [TestMethod]
    public void DescribeSymbolGap_SuccessfulOutput_ReturnsNull()
    {
        const string goodOutput =
            "-------------------------\n" +
            "Callstack for hr=0x80131509\n" +
            "    winui_app!App.OnLaunched\n" +
            "=========================";

        Assert.IsNull(XamlTriageService.DescribeSymbolGap(goodOutput, useSymbols: true));
    }

    [TestMethod]
    public void DescribeSymbolGap_SymbolsMissingButDecodeSucceeded_ReturnsNull()
    {
        // A symbol warning alone (without the createPointerObject decode failure) is not the gap.
        const string partial =
            "*** WARNING: Symbols for combase.dll not loaded/unavailable.\n" +
            "-------------------------\n" +
            "Callstack for hr=0x80131509";

        Assert.IsNull(XamlTriageService.DescribeSymbolGap(partial, useSymbols: true));
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Helpers.UiAudit;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiAuditEngineTests
{
    private static UiAuditEngine.Options Opts(params string[] checks) => new()
    {
        Checks = checks.Length == 0
            ? new HashSet<string>(UiAuditEngine.AllChecks, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(checks, StringComparer.OrdinalIgnoreCase),
        Profile = AuditProfile.Basic,
        NormalContrast = 4.5,
        LargeContrast = 3.0,
        WcagLevel = "AA",
    };

    [TestMethod]
    public void Names_InteractiveElementMissingName_ProducesFailure()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckNames));

        Assert.AreEqual(1, result.Summary.Fail);
        var issue = result.Issues.Single();
        Assert.AreEqual(UiAuditEngine.CheckNames, issue.RuleId);
        Assert.AreEqual(UiAuditEngine.SeverityFail, issue.Severity);
        Assert.AreEqual("btn-x", issue.Selector);
    }

    [TestMethod]
    public void Names_InteractiveElementWithName_Passes()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "Save", IsEnabled = true, IsKeyboardFocusable = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckNames));

        Assert.AreEqual(0, result.Issues.Length);
        Assert.AreEqual(1, result.Summary.Pass);
    }

    [TestMethod]
    public void Names_OffscreenElement_IsSkipped()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, IsOffscreen = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckNames));

        Assert.AreEqual(0, result.Issues.Length);
    }

    [TestMethod]
    public void Keyboard_InteractiveNotFocusable_ProducesWarning()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "Go", IsEnabled = true, IsKeyboardFocusable = false },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckKeyboard));

        Assert.AreEqual(1, result.Summary.Warn);
        Assert.AreEqual(UiAuditEngine.CheckKeyboard, result.Issues.Single().RuleId);
        Assert.AreEqual(UiAuditEngine.SeverityWarn, result.Issues.Single().Severity);
    }

    [TestMethod]
    public void Keyboard_FocusableUnnamed_ProducesFailure()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Edit", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "txt-name" },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckKeyboard));

        Assert.AreEqual(1, result.Summary.Fail);
        Assert.AreEqual(UiAuditEngine.CheckKeyboard, result.Issues.Single().RuleId);
    }

    [TestMethod]
    public void Keyboard_DisabledButFocusable_ProducesWarning()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "Save", IsEnabled = false, IsKeyboardFocusable = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckKeyboard));

        Assert.IsTrue(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckKeyboard && i.Severity == UiAuditEngine.SeverityWarn));
    }

    [TestMethod]
    public void Roles_ActionableUnknownControlType_ProducesWarning()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Unknown(50000)", Name = "Widget", IsInvokable = true, IsEnabled = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckRoles));

        Assert.AreEqual(1, result.Summary.Warn);
        Assert.AreEqual(UiAuditEngine.CheckRoles, result.Issues.Single().RuleId);
    }

    [TestMethod]
    public void Roles_ProperControlType_Passes()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "OK", IsInvokable = true, IsEnabled = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckRoles));

        Assert.AreEqual(0, result.Issues.Length);
        Assert.AreEqual(1, result.Summary.Pass);
    }

    [TestMethod]
    public void Roles_InvokablePane_ProducesWarning()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Pane", Name = "Wrapper", IsInvokable = true, IsEnabled = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckRoles));

        Assert.IsTrue(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckRoles && i.Severity == UiAuditEngine.SeverityWarn));
    }

    [TestMethod]
    public void Names_NameMatchingAutomationId_ProducesWarning()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "SaveButton", AutomationId = "SaveButton", IsEnabled = true, IsKeyboardFocusable = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckNames));

        Assert.IsTrue(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckNames && i.Severity == UiAuditEngine.SeverityWarn));
    }

    [TestMethod]
    public void ScreenReader_ReachableUnnamed_ProducesFailure()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckScreenReader));

        Assert.AreEqual(1, result.Summary.Fail);
        Assert.AreEqual(UiAuditEngine.CheckScreenReader, result.Issues.Single().RuleId);
    }

    [TestMethod]
    public void ScreenReader_CustomInvokableRole_ProducesWarning()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Custom", Name = "Widget", IsEnabled = true, IsInvokable = true, IsKeyboardFocusable = true },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckScreenReader));

        Assert.IsTrue(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckScreenReader && i.Severity == UiAuditEngine.SeverityWarn));
    }

    [TestMethod]
    public void Contrast_LowRatioTextElement_ProducesFailure()
    {
        var text = new UiElement { Id = "e0", Type = "Text", Name = "Hello", Width = 100, Height = 16 };
        var elements = new[] { text };

        // 3.0 ratio is below the AA normal-text threshold (4.5).
        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckContrast), _ => 3.0);

        Assert.AreEqual(1, result.Summary.Fail);
        var issue = result.Issues.Single();
        Assert.AreEqual(UiAuditEngine.CheckContrast, issue.RuleId);
        Assert.AreEqual(UiAuditEngine.SeverityFail, issue.Severity);
    }

    [TestMethod]
    public void Contrast_HighRatioTextElement_Passes()
    {
        var text = new UiElement { Id = "e0", Type = "Text", Name = "Hello", Width = 100, Height = 16 };
        var elements = new[] { text };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckContrast), _ => 21.0);

        Assert.AreEqual(0, result.Issues.Length);
        Assert.AreEqual(1, result.Summary.Pass);
    }

    [TestMethod]
    public void Contrast_LargeText_UsesRelaxedThreshold()
    {
        // 3.5:1 fails for normal text (< 4.5) but passes for large text (>= 3.0).
        var large = new UiElement { Id = "e0", Type = "Text", Name = "Big", Width = 200, Height = 30 };
        var elements = new[] { large };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckContrast), _ => 3.5);

        Assert.AreEqual(0, result.Summary.Fail);
        Assert.AreEqual(1, result.Summary.Pass);
    }

    [TestMethod]
    public void TabOrder_BackwardJump_ProducesWarning()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "First",  IsKeyboardFocusable = true, X = 10, Y = 100 },
            new UiElement { Id = "e1", Type = "Button", Name = "Second", IsKeyboardFocusable = true, X = 10, Y = 10 },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckTabOrder));

        Assert.IsTrue(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckTabOrder && i.Severity == UiAuditEngine.SeverityWarn));
    }

    [TestMethod]
    public void ChecksFilter_OnlySelectedRulesRun()
    {
        // Element fails names AND keyboard, but we only enable names.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = false },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckNames));

        Assert.IsTrue(result.Issues.All(i => i.RuleId == UiAuditEngine.CheckNames));
        Assert.IsFalse(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckKeyboard));
    }

    [TestMethod]
    public void Separator_ElementsAreIgnored()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "---", Name = "HWND 1: \"x\" (App, Class)" },
        };

        var result = UiAuditEngine.Run(elements, Opts());

        Assert.AreEqual(0, result.Issues.Length);
    }

    [TestMethod]
    public void Chrome_ScrollBarThumb_NotFlagged()
    {
        // A Thumb that is interactive but not keyboard-focusable would normally trip keyboard +
        // screen-reader; as scrollbar chrome it must be suppressed.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Thumb", Name = null, IsEnabled = true, IsInvokable = true, IsKeyboardFocusable = false, Selector = "thumb-x" },
        };

        var result = UiAuditEngine.Run(elements, Opts());

        Assert.AreEqual(0, result.Issues.Length, "scrollbar thumb should not be flagged");
    }

    [TestMethod]
    public void Chrome_ScrollBarIncrementButton_NotFlagged()
    {
        // A scrollbar increment button (system-generated name, generic Button type) that is not
        // keyboard-focusable would trip keyboard/screen-reader without name-based chrome suppression.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "Vertical Small Increase", IsEnabled = true, IsInvokable = true, IsKeyboardFocusable = false, Selector = "vsi" },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckKeyboard, UiAuditEngine.CheckScreenReader, UiAuditEngine.CheckNames));

        Assert.AreEqual(0, result.Issues.Length, "scrollbar increment button should not be flagged");
    }

    [TestMethod]
    public void Chrome_TitleBarCaptionButton_NotFlagged()
    {
        // A caption button under a TitleBar ancestor that is not keyboard-focusable would trip
        // keyboard/screen-reader; TitleBar ancestry suppresses it.
        var elements = new[]
        {
            new UiElement
            {
                Id = "e0", Type = "Button", Name = "Close", IsEnabled = true, IsInvokable = true,
                IsKeyboardFocusable = false, Selector = "btn-close",
                AncestorPath = ["Window", "TitleBar"],
            },
        };

        var result = UiAuditEngine.Run(elements, Opts());

        Assert.AreEqual(0, result.Issues.Length, "title-bar caption button should not be flagged");
    }

    [TestMethod]
    public void LegitButtonNamedClose_WithoutChromeContext_IsStillFlagged()
    {
        // A real app control literally named "Close" that is NOT a titlebar/scrollbar part must
        // NOT be suppressed: its keyboard warning (interactive but not focusable) must still fire.
        var elements = new[]
        {
            new UiElement
            {
                Id = "e0", Type = "Button", Name = "Close", IsEnabled = true, IsInvokable = true,
                IsKeyboardFocusable = false, Selector = "app-close",
            },
        };

        var result = UiAuditEngine.Run(elements, Opts(UiAuditEngine.CheckKeyboard, UiAuditEngine.CheckScreenReader, UiAuditEngine.CheckNames));

        Assert.IsTrue(result.Issues.Length > 0, "a non-chrome app button named 'Close' must still be flagged");
        Assert.IsTrue(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckKeyboard), "expected the keyboard not-focusable warning to fire");
    }

    [TestMethod]
    public void Chrome_UnnamedTitleBarButton_NotFlaggedForMissingName()
    {
        // An unnamed non-client button under the title bar must not raise a missing-name failure.
        var elements = new[]
        {
            new UiElement
            {
                Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true,
                Selector = "chrome-btn", AncestorPath = ["Window", "TitleBar"],
            },
        };

        var result = UiAuditEngine.Run(elements, Opts());

        Assert.AreEqual(0, result.Summary.Fail, "unnamed title-bar chrome should not fail names/keyboard/screen-reader");
        Assert.IsFalse(result.Issues.Any(i => i.RuleId == UiAuditEngine.CheckNames));
    }
}

[TestClass]
public class ContrastAnalyzerTests
{
    private static byte[] SolidBgra(int w, int h, byte r, byte g, byte b)
    {
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            buf[i * 4 + 0] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = 255;
        }
        return buf;
    }

    [TestMethod]
    public void BlackTextOnWhite_YieldsHighContrast()
    {
        const int w = 10, h = 10;
        var buf = SolidBgra(w, h, 255, 255, 255); // white
        // Paint ~25% of pixels black (text glyphs).
        for (var i = 0; i < 25; i++)
        {
            buf[i * 4 + 0] = 0;
            buf[i * 4 + 1] = 0;
            buf[i * 4 + 2] = 0;
        }

        var ratio = ContrastAnalyzer.ComputeContrastRatio(buf, w, h, new ContrastAnalyzer.PixelRect(0, 0, w, h));

        Assert.IsNotNull(ratio);
        Assert.IsTrue(ratio > 15.0, $"expected high contrast, got {ratio}");
    }

    [TestMethod]
    public void GreyOnWhite_IsBelowAaThreshold()
    {
        const int w = 10, h = 10;
        var buf = SolidBgra(w, h, 255, 255, 255); // white
        // Paint ~25% mid-grey (#777) — a classic low-contrast case (~4.48:1).
        for (var i = 0; i < 25; i++)
        {
            buf[i * 4 + 0] = 0x77;
            buf[i * 4 + 1] = 0x77;
            buf[i * 4 + 2] = 0x77;
        }

        var ratio = ContrastAnalyzer.ComputeContrastRatio(buf, w, h, new ContrastAnalyzer.PixelRect(0, 0, w, h));

        Assert.IsNotNull(ratio);
        Assert.IsTrue(ratio < 4.5, $"expected sub-AA contrast, got {ratio}");
    }

    [TestMethod]
    public void SolidRegion_IsNotMeasured_ReturnsNull()
    {
        // A uniform fill has no glyph cluster — it is "not measured" rather than a fabricated ~1:1,
        // which previously produced phantom sub-AA contrast failures on solid text backgrounds.
        const int w = 8, h = 8;
        var buf = SolidBgra(w, h, 200, 200, 200);

        var ratio = ContrastAnalyzer.ComputeContrastRatio(buf, w, h, new ContrastAnalyzer.PixelRect(0, 0, w, h));

        Assert.IsNull(ratio, $"expected null (not measured), got {ratio}");
    }

    [TestMethod]
    public void SparseSingleGlyphOnWhite_IsNotMeasured_ReturnsNull()
    {
        // A handful of dark pixels on a large white rect (short text) must NOT collapse to a
        // fabricated sub-AA number; the glyph cluster is below the coverage floor -> not measured.
        const int w = 40, h = 40; // 1600 opaque px
        var buf = SolidBgra(w, h, 255, 255, 255);
        for (var i = 0; i < 3; i++) // 3 black pixels, far below the 8px / 0.5% floor
        {
            buf[i * 4 + 0] = 0;
            buf[i * 4 + 1] = 0;
            buf[i * 4 + 2] = 0;
        }

        var ratio = ContrastAnalyzer.ComputeContrastRatio(buf, w, h, new ContrastAnalyzer.PixelRect(0, 0, w, h));

        // Must not be a fabricated sub-AA ratio. Null is the expected "not measured" outcome.
        Assert.IsTrue(ratio is null || ratio >= 4.5, $"expected null or high contrast, got {ratio}");
    }

    [TestMethod]
    public void MostlyTransparentRect_IsNotMeasured_ReturnsNull()
    {
        // A rect that is mostly transparent (layered/empty) must not be scored as its raw RGB.
        const int w = 10, h = 10;
        var buf = SolidBgra(w, h, 0, 0, 0); // black RGB...
        for (var i = 0; i < w * h; i++)
        {
            buf[i * 4 + 3] = 0; // ...but fully transparent
        }
        // Make a small opaque minority (< 40%).
        for (var i = 0; i < 10; i++)
        {
            buf[i * 4 + 3] = 255;
        }

        var ratio = ContrastAnalyzer.ComputeContrastRatio(buf, w, h, new ContrastAnalyzer.PixelRect(0, 0, w, h));

        Assert.IsNull(ratio, $"expected null (not measured) for mostly-transparent rect, got {ratio}");
    }

    [TestMethod]
    public void FullyTransparentRect_ReturnsNull()
    {
        const int w = 8, h = 8;
        var buf = SolidBgra(w, h, 0, 0, 0);
        for (var i = 0; i < w * h; i++)
        {
            buf[i * 4 + 3] = 0; // all transparent
        }

        var ratio = ContrastAnalyzer.ComputeContrastRatio(buf, w, h, new ContrastAnalyzer.PixelRect(0, 0, w, h));

        Assert.IsNull(ratio);
    }

    [TestMethod]
    public void TransparentGlyphPixels_AreIgnored_NotScoredAsBlack()
    {
        // Grey glyphs (opaque) on white, plus scattered transparent-black pixels that must be
        // ignored (not pulled in as black outliers that would skew the ratio).
        const int w = 20, h = 20; // 400 px
        var buf = SolidBgra(w, h, 255, 255, 255);
        // 100 opaque grey glyph pixels (25% coverage).
        for (var i = 0; i < 100; i++)
        {
            buf[i * 4 + 0] = 0x77;
            buf[i * 4 + 1] = 0x77;
            buf[i * 4 + 2] = 0x77;
        }
        // 50 transparent pure-black pixels elsewhere — must be skipped by the alpha guard.
        for (var i = 300; i < 350; i++)
        {
            buf[i * 4 + 0] = 0;
            buf[i * 4 + 1] = 0;
            buf[i * 4 + 2] = 0;
            buf[i * 4 + 3] = 0;
        }

        var ratio = ContrastAnalyzer.ComputeContrastRatio(buf, w, h, new ContrastAnalyzer.PixelRect(0, 0, w, h));

        // Should reflect grey-on-white (~4.4:1), NOT black-on-white (~21:1).
        Assert.IsNotNull(ratio);
        Assert.IsTrue(ratio < 6.0 && ratio > 3.5, $"expected grey-on-white ratio, got {ratio}");
    }

    [TestMethod]
    public void DegenerateRect_ReturnsNull()
    {
        var buf = SolidBgra(4, 4, 0, 0, 0);
        Assert.IsNull(ContrastAnalyzer.ComputeContrastRatio(buf, 4, 4, new ContrastAnalyzer.PixelRect(0, 0, 0, 0)));
        Assert.IsNull(ContrastAnalyzer.ComputeContrastRatio(buf, 4, 4, new ContrastAnalyzer.PixelRect(10, 10, 2, 2)));
    }

    [TestMethod]
    public void KnownColors_ProduceExpectedRatio()
    {
        // Black vs white is exactly 21:1 by WCAG definition.
        var black = ContrastAnalyzer.RelativeLuminance(0, 0, 0);
        var white = ContrastAnalyzer.RelativeLuminance(255, 255, 255);
        var ratio = ContrastAnalyzer.ContrastRatio(black, white);
        Assert.AreEqual(21.0, ratio, 0.01);
    }
}

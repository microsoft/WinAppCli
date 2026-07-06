// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Audit_WithoutApp_ReturnsError()
    {
        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Audit_MissingName_FailsAndExitsNonZero()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ruleId\": \"names\"");
        StringAssert.Contains(output, "\"severity\": \"fail\"");
        StringAssert.Contains(output, "\"fail\": 1");
    }

    [TestMethod]
    public async Task Audit_CleanTree_PassesAndExitsZero()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = "OK", IsEnabled = true, IsKeyboardFocusable = true, IsInvokable = true },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--area", "keyboard", "--area", "roles", "--json"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 0");
    }

    [TestMethod]
    public async Task Audit_InvalidLevel_ReturnsError()
    {
        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--level", "deep", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Audit_WritesReportFile()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        ];

        var reportPath = Path.Combine(_tempDirectory.FullName, "audit.json");
        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--json", "-o", reportPath]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(File.Exists(reportPath), "expected report file to be written");
        var content = await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken);
        StringAssert.Contains(content, "\"ruleId\": \"names\"");
    }

    [TestMethod]
    public async Task Audit_ContrastCaptureUnavailable_SkipsContrastGracefully()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "Hello", Width = 100, Height = 16 },
        ];
        _fakeUia.WindowCaptureException = new InvalidOperationException("no capture");

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        // No fail-severity issues since contrast could not be measured.
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 0");
    }

    [TestMethod]
    public async Task Audit_AreaNames_RunsOnlyNamesArea()
    {
        // Element fails names (no name) AND keyboard (interactive, not focusable), but we scope to
        // the names area only, so only a names finding should surface.
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = false, Selector = "btn-x" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ruleId\": \"names\"");
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"ruleId\":\\s*\"keyboard\""));
    }

    [TestMethod]
    public async Task Audit_LevelBasic_OmitsTabOrderFromKeyboardArea()
    {
        // Backward-jumping focusable elements: tab-order warns only under the thorough level.
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Button", Name = "First",  IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 100 },
            new UiElement { Id = "e1", Type = "Button", Name = "Second", IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 10 },
        ];

        var basicCmd = GetRequiredService<UiAuditCommand>();
        await ParseAndInvokeWithCaptureAsync(basicCmd, ["-a", "TestApp", "--area", "keyboard", "--level", "basic", "--json"]);
        StringAssert.DoesNotMatch(TestAnsiConsole.Output,
            new System.Text.RegularExpressions.Regex("\"ruleId\":\\s*\"tab-order\""));
    }

    [TestMethod]
    public async Task Audit_LevelThorough_ReportsTabOrder()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Button", Name = "First",  IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 100 },
            new UiElement { Id = "e1", Type = "Button", Name = "Second", IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 10 },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "keyboard", "--level", "thorough", "--json"]);
        StringAssert.Contains(TestAnsiConsole.Output, "\"ruleId\": \"tab-order\"");
    }

    [TestMethod]
    public async Task Audit_InvalidArea_ReturnsError()
    {
        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "bogus", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Audit_AllAreasBasic_DeDuplicatesMissingNameAcrossAreas()
    {
        // No --area/--level: defaults to all areas, basic level. A single unnamed focusable
        // element trips the SAME missing-name defect in names, keyboard, and screen-reader. Cross-
        // area de-duplication collapses it to ONE failure (the canonical names finding) rather than
        // triple-counting it.
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ruleId\": \"names\"");
        StringAssert.Contains(output, "\"fail\": 1");
        // The duplicate missing-name findings from keyboard/screen-reader are collapsed away.
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"fail\":\\s*3"));
    }

    [TestMethod]
    public async Task Audit_AreaEvents_ReturnsError()
    {
        // events is a reserved no-op area, not user-selectable for now.
        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "events", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Audit_Contrast_OutOfWindowElement_IsNotMeasured()
    {
        // The captured buffer belongs to the session's root window (HWND 100). An element on a
        // different HWND (a popup, 200) must be marked "not measured" rather than sampled against
        // the wrong pixels — so it produces no contrast failure even though the buffer is low
        // contrast. The in-window element IS scored (and fails).
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234,
            ProcessName = "TestApp",
            WindowTitle = "Test Window",
            WindowHandle = 100,
        };

        // 20x20 grey (#999) glyphs on white → a clear sub-AA (~2.8:1) failure for normal text.
        const int w = 20, h = 20;
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            byte v = i < 100 ? (byte)0x99 : (byte)0xFF; // 25% grey glyph coverage, rest white
            buf[i * 4 + 0] = v;
            buf[i * 4 + 1] = v;
            buf[i * 4 + 2] = v;
            buf[i * 4 + 3] = 255;
        }
        _fakeUia.WindowCaptureResult = (buf, w, h, 0, 0);

        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "InWin", Width = w, Height = 16, X = 0, Y = 0, WindowHandle = 100, Selector = "in-win" },
            new UiElement { Id = "e1", Type = "Text", Name = "Popup", Width = w, Height = 16, X = 0, Y = 0, WindowHandle = 200, Selector = "popup" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        var output = TestAnsiConsole.Output;
        Assert.AreEqual(1, exitCode, "the in-window low-contrast text should fail");
        StringAssert.Contains(output, "\"fail\": 1");
        StringAssert.Contains(output, "\"selector\": \"in-win\"");
        // The out-of-window popup element must NOT appear as a contrast finding.
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"selector\":\\s*\"popup\""));
    }

    // A 20x20 buffer whose glyph pixels are mid-grey (#696969, ~5.5:1 on white) — above the AA
    // normal-text threshold (4.5) but below AAA (7.0).
    private static (byte[] Pixels, int W, int H, int OX, int OY) MidGreyOnWhiteCapture()
    {
        const int w = 20, h = 20;
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            byte v = i < 100 ? (byte)0x69 : (byte)0xFF; // 25% grey glyph coverage, rest white
            buf[i * 4 + 0] = v;
            buf[i * 4 + 1] = v;
            buf[i * 4 + 2] = v;
            buf[i * 4 + 3] = 255;
        }
        return (buf, w, h, 0, 0);
    }

    [TestMethod]
    public async Task Audit_ContrastLevelBasic_MidGreyPassesUnderAa()
    {
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234, ProcessName = "TestApp", WindowTitle = "Test Window", WindowHandle = 100,
        };
        _fakeUia.WindowCaptureResult = MidGreyOnWhiteCapture();
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "Grey", Width = 20, Height = 16, X = 0, Y = 0, WindowHandle = 100, Selector = "grey" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        Assert.AreEqual(0, exitCode, "mid-grey (~5.5:1) meets the AA 4.5 threshold used by --level basic");
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 0");
    }

    [TestMethod]
    public async Task Audit_ContrastLevelThorough_UsesAaaThreshold()
    {
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234, ProcessName = "TestApp", WindowTitle = "Test Window", WindowHandle = 100,
        };
        _fakeUia.WindowCaptureResult = MidGreyOnWhiteCapture();
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "Grey", Width = 20, Height = 16, X = 0, Y = 0, WindowHandle = 100, Selector = "grey" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--level", "thorough", "--json"]);

        Assert.AreEqual(1, exitCode, "mid-grey (~5.5:1) fails the AAA 7.0 threshold used by --level thorough");
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"fail\": 1");
        StringAssert.Contains(output, "AAA");
        StringAssert.Contains(output, "7.0:1");
    }
}

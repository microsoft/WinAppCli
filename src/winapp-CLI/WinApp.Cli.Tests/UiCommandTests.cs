// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiCommandTests : BaseCommandTests
{
    private FakeUiAutomationService _fakeUia = null!;
    private FakeUiSessionService _fakeSession = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeUia = new FakeUiAutomationService();
        _fakeSession = new FakeUiSessionService();
        return services
            .AddSingleton<IUiAutomationService>(_fakeUia)
            .AddSingleton<IUiSessionService>(_fakeSession);
    }

    [TestMethod]
    public async Task Status_WithApp_ReturnsSuccess()
    {
        var command = GetRequiredService<UiStatusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"processId\": 1234");
    }

    [TestMethod]
    public async Task Status_WithoutApp_ReturnsError()
    {
        var command = GetRequiredService<UiStatusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Inspect_ReturnsElements()
    {
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Name = "Test", IsEnabled = true, Width = 800, Height = 600 },
            new UiElement { Id = "e1", Type = "Button", Name = "OK", IsEnabled = true, Width = 100, Height = 30 }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"id\": \"e0\"");
        StringAssert.Contains(TestAnsiConsole.Output, "\"type\": \"Window\"");
    }

    [TestMethod]
    public async Task Search_ReturnsMatches()
    {
        _fakeUia.SearchResult = [
            new UiElement { Id = "e0", Type = "Button", Name = "OK", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = "Cancel", IsEnabled = true }
        ];

        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"matchCount\": 2");
    }

    [TestMethod]
    public async Task Search_WithoutSelector_ReturnsError()
    {
        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Invoke_WithNameSelector_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit" };
        _fakeUia.InvokeResult = "InvokePattern";

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Submit", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"pattern\": \"InvokePattern\"");
    }

    [TestMethod]
    public async Task Invoke_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#NonExistent", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Invoke_ByElementId_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "TestButton" };

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task GetProperty_ReturnsProperties()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK", IsEnabled = true };
        _fakeUia.PropertiesResult = new Dictionary<string, object?> { ["IsEnabled"] = true, ["Name"] = "OK" };

        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"elementId\": \"e0\"");
    }

    [TestMethod]
    public async Task Screenshot_Json_ReturnsFilePath()
    {
        // Small 1x1 BGRA pixel for the fake
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"filePath\":");
        StringAssert.Contains(TestAnsiConsole.Output, "\"width\": 1");
    }

    [TestMethod]
    public async Task SetValue_WithText_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Edit", Name = "TestEdit" };

        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "Hello", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task SetValue_WithoutText_ReturnsError()
    {
        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Focus_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK" };

        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task GetValue_ReturnsText()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Document", Name = "Text editor", Selector = "doc-texteditor-53ad" };

        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["doc-texteditor-53ad", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"text\":");
    }

    [TestMethod]
    public async Task GetValue_WithoutSelector_ReturnsError()
    {
        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_ExistingElement_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit" };

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "-a", "TestApp", "--timeout", "1000"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_NonExistent_TimesOut()
    {
        _fakeUia.SearchResult = [];

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#NonExistent", "-a", "TestApp", "--timeout", "500"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task ListWindows_ReturnsWindows()
    {
        _fakeUia.WindowsByTitleResult = [
            (1001, 1234, "Main Window"),
            (1002, 1234, "Popup")
        ];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 1001");
    }
}

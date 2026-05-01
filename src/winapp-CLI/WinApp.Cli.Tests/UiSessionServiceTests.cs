// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiSessionServiceTests
{
    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [TestMethod]
    public void UiSessionInfo_IsExplicitWindow_DefaultsToFalse()
    {
        var info = new UiSessionInfo();
        Assert.IsFalse(info.IsExplicitWindow);
    }

    [TestMethod]
    public async Task ResolveSessionAsync_ByHwnd_SetsIsExplicitWindowTrue()
    {
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd == 0)
        {
            Assert.Inconclusive("No console window available — cannot exercise --window resolution.");
        }

        var service = CreateService();

        var session = await service.ResolveSessionAsync(app: null, hwnd: consoleHwnd, CancellationToken.None);

        Assert.IsTrue(session.IsExplicitWindow,
            "Sessions resolved via --window must be marked explicit so inspect/search/find don't expand to other windows (#472).");
        Assert.AreEqual((long)consoleHwnd, session.WindowHandle);
    }

    [TestMethod]
    public async Task ResolveSessionAsync_ByPid_LeavesIsExplicitWindowFalse()
    {
        var service = CreateService();
        var currentProcess = System.Diagnostics.Process.GetCurrentProcess();

        var session = await service.ResolveSessionAsync(
            app: currentProcess.Id.ToString(),
            hwnd: null,
            CancellationToken.None);

        Assert.IsFalse(session.IsExplicitWindow,
            "Only --window should mark a session as explicit; --app/PID resolution must leave it false.");
        Assert.AreEqual(currentProcess.Id, session.ProcessId);
    }

    private static UiSessionService CreateService()
        => new(new StubUiAutomation(), NullLogger<UiSessionService>.Instance);

    /// <summary>
    /// Minimal IUiAutomationService stub: returns no windows so the resolver doesn't try to
    /// auto-select on top of the underlying process lookup.
    /// </summary>
    private sealed class StubUiAutomation : IUiAutomationService
    {
        public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery) => [];
        public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid) => [];

        public Task<UiElement[]> InspectAsync(UiSessionInfo session, string? elementId, int depth, CancellationToken ct) => Task.FromResult<UiElement[]>([]);
        public Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct) => Task.FromResult<UiElement[]>([]);
        public Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct) => Task.FromResult<UiElement[]>([]);
        public Task<UiElement?> FindSingleElementAsync(UiSessionInfo session, SelectorExpression selector, CancellationToken ct) => Task.FromResult<UiElement?>(null);
        public Task<Dictionary<string, object?>> GetPropertiesAsync(UiSessionInfo session, UiElement element, string? propertyName, CancellationToken ct) => Task.FromResult(new Dictionary<string, object?>());
        public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiSessionInfo session, string? elementId, bool captureScreen, CancellationToken ct) => Task.FromResult((Array.Empty<byte>(), 0, 0));
        public Task<string> InvokeAsync(UiSessionInfo session, UiElement element, CancellationToken ct) => Task.FromResult("");
        public Task SetValueAsync(UiSessionInfo session, UiElement element, string text, CancellationToken ct) => Task.CompletedTask;
        public Task FocusAsync(UiSessionInfo session, UiElement element, CancellationToken ct) => Task.CompletedTask;
        public Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct) => Task.CompletedTask;
        public Task ScrollContainerAsync(UiSessionInfo session, UiElement element, string? direction, string? to, CancellationToken ct) => Task.CompletedTask;
        public Task<UiElement?> GetFocusedElementAsync(UiSessionInfo session, CancellationToken ct) => Task.FromResult<UiElement?>(null);
        public Task<string?> GetTextAsync(UiSessionInfo session, UiElement element, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}


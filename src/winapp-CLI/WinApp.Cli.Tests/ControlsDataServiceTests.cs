// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ControlsDataService"/> cache-clearing behaviour. We
/// don't exercise <see cref="ControlsDataService.GetEngine"/> end-to-end here
/// because that would hit the network (or embedded-fallback path) — those
/// surfaces are tested via the fetcher unit tests and the command tests use a
/// fake.
/// </summary>
[TestClass]
public class ControlsDataServiceTests : BaseCommandTests
{
    [TestMethod]
    public void ClearCache_DeletesBothFetcherCacheSubdirectoriesWhenPresent()
    {
        var service = GetRequiredService<IControlsDataService>();
        var globalDir = _testCacheDirectory;

        var winui = Path.Combine(globalDir.FullName, "cache", "controls", "winui-gallery");
        var toolkit = Path.Combine(globalDir.FullName, "cache", "controls", "toolkit");
        Directory.CreateDirectory(winui);
        Directory.CreateDirectory(toolkit);
        File.WriteAllText(Path.Combine(winui, "scenarios.json"), "[]");
        File.WriteAllText(Path.Combine(toolkit, "scenarios.json"), "[]");

        service.ClearCache();

        Assert.IsFalse(Directory.Exists(winui), "WinUI Gallery cache directory should be deleted.");
        Assert.IsFalse(Directory.Exists(toolkit), "Toolkit cache directory should be deleted.");
    }

    [TestMethod]
    public void ClearCache_NoCacheToDelete_DoesNotThrow()
    {
        var service = GetRequiredService<IControlsDataService>();

        // Cache dirs never existed — must be a no-op, not an error.
        service.ClearCache();
    }
}

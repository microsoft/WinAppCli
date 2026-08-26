// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="IDotNetProjectRestoreService"/>'s project-selection contract. The happy path and the
/// nuget.config behavior are covered end to end in <see cref="WorkspaceSetupServiceConfigModeTests"/>; these
/// pin the two counts the service must handle without touching disk or invoking dotnet.
/// </summary>
[TestClass]
public class DotNetProjectRestoreServiceTests : BaseCommandTests
{
    private FakeDotNetService _dotnet = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _dotnet = new FakeDotNetService();
        return services.AddSingleton<IDotNetService>(_dotnet);
    }

    /// <summary>
    /// RestoreAsync is public API, so "no project here" must be reported rather than indexing into an empty
    /// list. The current caller only delegates here after detecting a .csproj, so without this the failure
    /// mode would be an IndexOutOfRangeException the first time another caller appeared.
    /// </summary>
    [TestMethod]
    public async Task RestoreAsync_NoProjectFound_ReturnsErrorWithoutThrowing()
    {
        var service = GetRequiredService<IDotNetProjectRestoreService>();

        var exitCode = await service.RestoreAsync(_tempDirectory, _tempDirectory, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        Assert.IsEmpty(_dotnet.InheritedCalls, "no project means nothing should be handed to dotnet restore");
    }

    /// <summary>
    /// Several projects in one directory is ambiguous: restore takes no project argument, so it reports them
    /// instead of guessing, and must not restore any of them.
    /// </summary>
    [TestMethod]
    public async Task RestoreAsync_MultipleProjects_ReportsAmbiguityWithoutRestoring()
    {
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "One.csproj"), "<Project />", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "Two.csproj"), "<Project />", TestContext.CancellationToken);

        var service = GetRequiredService<IDotNetProjectRestoreService>();

        var exitCode = await service.RestoreAsync(_tempDirectory, _tempDirectory, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        Assert.IsEmpty(_dotnet.InheritedCalls, "an ambiguous directory must not restore an arbitrary project");
    }
}

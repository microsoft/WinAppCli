// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class RestoreCommandTests() : BaseCommandTests(configPaths: false)
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services.AddSingleton<IWorkspaceSetupService, CaptureWorkspaceSetupService>();
    }

    [TestMethod]
    public async Task RestoreCommand_WithoutBaseDirectory_UsesCurrentDirectory()
    {
        // Arrange
        var command = GetRequiredService<RestoreCommand>();
        var setupService = GetRequiredService<IWorkspaceSetupService>() as CaptureWorkspaceSetupService;
        Assert.IsNotNull(setupService);

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, Array.Empty<string>());

        // Assert
        Assert.AreEqual(0, exitCode, "Restore command should complete successfully");
        Assert.IsNotNull(setupService.CapturedOptions, "Restore command should call workspace setup");
        Assert.AreEqual(_tempDirectory.FullName, setupService.CapturedOptions.BaseDirectory.FullName);
    }

    private sealed class CaptureWorkspaceSetupService : IWorkspaceSetupService
    {
        public WorkspaceSetupOptions? CapturedOptions { get; private set; }

        public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null)
        {
            return null;
        }

        public Task<(int InstalledCount, int ErrorCount)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken)
        {
            return Task.FromResult((0, 0));
        }

        public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
        {
            CapturedOptions = options;
            return Task.FromResult(0);
        }
    }
}

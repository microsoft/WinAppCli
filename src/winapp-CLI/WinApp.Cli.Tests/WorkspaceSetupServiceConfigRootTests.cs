// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Regression tests for how <see cref="WorkspaceSetupService"/> roots the NuGet settings hierarchy.
/// Split out from <see cref="WorkspaceSetupServiceTests"/> to keep each file within the repository's
/// file-size guidance.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceConfigRootTests : BaseCommandTests
{
    [TestMethod]
    public async Task SetupWorkspace_RootsNuGetSettingsAtConfigDir_NotWorkingDirectory()
    {
        // Regression test for `init <dir>` / `restore --config-dir <dir>`: the NuGet settings hierarchy must
        // be resolved from the selected config directory, not the process working directory. Without
        // SetupWorkspaceAsync calling NugetSourceProvider.SetConfigRoot(options.ConfigDir), a project's
        // private feed / credentials / globalPackagesFolder would be silently ignored whenever the working
        // directory differs from the target project directory — so this test pins that fix.

        // The working directory (what CurrentDirectoryProvider is rooted at, per BaseCommandTests) declares
        // one feed; a separate config directory declares a DIFFERENT feed. Both <clear /> inherited sources,
        // so each directory resolves exactly one distinct source (and the config dir's <clear /> also
        // removes the parent working-directory feed from its own hierarchy).
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="working-only-feed" value="working-feed" />
              </packageSources>
            </configuration>
            """);

        var configDir = _tempDirectory.CreateSubdirectory("project-root");
        await File.WriteAllTextAsync(Path.Join(configDir.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="configdir-only-feed" value="configdir-feed" />
              </packageSources>
            </configuration>
            """);

        // The shared singleton that SetupWorkspaceAsync re-roots is the SAME instance that answers the
        // assertion below.
        var sourceProvider = GetRequiredService<NugetSourceProvider>();
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = configDir,
            ConfigDir = configDir,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            ForceLatestBuildTools = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Assert: after setup the shared source provider resolves the config directory's feed — proving
        // SetupWorkspaceAsync rooted NuGet settings at ConfigDir rather than the working directory.
        var sources = sourceProvider.GetRepositoriesForPackage("Any.Package")
            .Select(r => r.PackageSource.Name)
            .ToList();

        CollectionAssert.Contains(sources, "configdir-only-feed", "NuGet settings must be resolved from the selected ConfigDir.");
        CollectionAssert.DoesNotContain(sources, "working-only-feed", "NuGet settings must NOT be resolved from the process working directory when an explicit ConfigDir is provided.");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using System.CommandLine;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public abstract class BaseCommandTests(bool configPaths = true, bool verboseLogging = true)
{
    private protected DirectoryInfo _tempDirectory = null!;
    private protected DirectoryInfo _testWinappDirectory = null!;
    private protected DirectoryInfo _testCacheDirectory = null!;
    private protected IConfigService _configService = null!;
    private protected IBuildToolsService _buildToolsService = null!;

    private ServiceProvider _serviceProvider = null!;
    private protected OutputCapture ConsoleStdOut { private set; get; } = null!;
    private protected OutputCapture ConsoleStdErr { private set; get; } = null!;

    public TestContext TestContext { get; set; } = null!;
    private protected TaskContext TestTaskContext { private set; get; } = null!;
    private protected GroupableTask TestTask { private set; get; } = null!;
    private protected Lock RenderLock { private set; get; } = null!;
    private protected TestConsole TestAnsiConsole { private set; get; } = null!;

    [TestInitialize]
    public void SetupBase()
    {
        TestAnsiConsole = new TestConsole();

        ConsoleStdOut = new OutputCapture(TestAnsiConsole.Profile.Out.Writer);
        ConsoleStdErr = new OutputCapture(Console.Error);

        // Create a temporary directory for testing
        _tempDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"{this.GetType().Name}_{Guid.NewGuid():N}"));
        _tempDirectory.Create();

        // Set up a temporary winapp directory for testing (isolates tests from real winapp directory)
        _testWinappDirectory = _tempDirectory.CreateSubdirectory(".winapp");

        var services = new ServiceCollection()
            .ConfigureServices(ConsoleStdOut)
            .ConfigureCommands();
        services =
            ConfigureServices(services)
            // Override services
            .AddSingleton<ICurrentDirectoryProvider>(sp => new CurrentDirectoryProvider(_tempDirectory.FullName))
            .AddSingleton<IAnsiConsole>(TestAnsiConsole)
            .AddLogging(b =>
            {
                b.ClearProviders();
                b.AddTextWriterLogger(ConsoleStdOut, ConsoleStdErr);
                // Use Debug level for verbose logging, Information level for non-verbose
                b.SetMinimumLevel(verboseLogging ? LogLevel.Debug : LogLevel.Information);
            });

        _serviceProvider = services.BuildServiceProvider();

        TestTask = new GroupableTask("Dummy Task", null);

        RenderLock = new Lock();
        TestTaskContext = new TaskContext(TestTask, null, TestAnsiConsole, GetRequiredService<ILogger<TaskContext>>(), RenderLock);

        // Set up services with test cache directory
        if (configPaths)
        {
            _configService = GetRequiredService<IConfigService>();
            _configService.ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));

            var directoryService = GetRequiredService<IWinappDirectoryService>();
            _testCacheDirectory = _tempDirectory.CreateSubdirectory(".winappcache");
            directoryService.SetCacheDirectoryForTesting(_testCacheDirectory);

            // Wire up test cache directory for FakeNugetService if present
            if (GetRequiredService<INugetService>() is FakeNugetService fakeNuget)
            {
                fakeNuget.CacheDirectory = _testCacheDirectory;
            }

            _buildToolsService = GetRequiredService<IBuildToolsService>();
        }
    }

    protected async Task<int> ParseAndInvokeWithCaptureAsync(Command command, string[] manifestArgs)
    {
        var parseResult = command.Parse(manifestArgs);
        parseResult.InvocationConfiguration.Output = TestAnsiConsole.Profile.Out.Writer;
        parseResult.InvocationConfiguration.Error = ConsoleStdErr;
        return await parseResult.InvokeAsync(parseResult.InvocationConfiguration, cancellationToken: TestContext.CancellationToken);
    }

    protected virtual IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services;
    }

    [TestCleanup]
    public void CleanupBase()
    {
        _serviceProvider?.Dispose();
        ConsoleStdOut?.Dispose();
        ConsoleStdErr?.Dispose();

        // Clean up temporary files and directories
        _tempDirectory.Refresh();
        if (_tempDirectory.Exists)
        {
            try
            {
                _tempDirectory.Delete(true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    protected T GetRequiredService<T>() where T : notnull
    {
        return _serviceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Ensures a single NuGet package is available in the test NuGet cache by copying it
    /// from the real global NuGet cache if available, falling back to downloading from NuGet.org.
    /// <para>
    /// This avoids expensive HTTP downloads that can timeout (100 s default) when many tests
    /// run in parallel (12-way method-level parallelism) and all try to download large packages
    /// like <c>Microsoft.WindowsAppSDK.Runtime</c> simultaneously.
    /// </para>
    /// </summary>
    protected async Task EnsurePackageInTestCacheAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        var nugetService = GetRequiredService<INugetService>();
        var testPackagesDir = nugetService.GetNuGetGlobalPackagesDir();
        var targetDir = new DirectoryInfo(Path.Combine(testPackagesDir.FullName, packageId.ToLowerInvariant(), version));

        if (targetDir.Exists)
        {
            return;
        }

        // Try to copy from the real NuGet cache (fast, no network needed).
        // For EndToEndTests, 'dotnet build' already downloads packages here.
        // For PackageCommandTests, previous test runs will have cached them.
        var realCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", packageId.ToLowerInvariant(), version);
        var realCacheDir = new DirectoryInfo(realCachePath);

        if (realCacheDir.Exists)
        {
            CopyDirectoryRecursive(realCacheDir, targetDir);
            return;
        }

        // Fallback: download from NuGet.org
        var packageInstallService = GetRequiredService<IPackageInstallationService>();
        await packageInstallService.EnsurePackageAsync(
            _testCacheDirectory, packageId, TestTaskContext,
            version: version, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Recursively copies a directory and all its contents to a new location.
    /// </summary>
    private static void CopyDirectoryRecursive(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source.FullName, file.FullName);
            var destPath = Path.Combine(target.FullName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            file.CopyTo(destPath, overwrite: true);
        }
    }
}

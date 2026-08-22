// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class RuntimePrepareCommandTests() : BaseCommandTests(logLevel: LogLevel.None)
{
    private readonly FakeRuntimeDeploymentService _deployment = new();

    protected override IServiceCollection ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IWindowsAppRuntimeDeploymentService>(_deployment);

    [TestMethod]
    public async Task JsonSuccessIsSingleDeterministicDocument()
    {
        var output = _tempDirectory.CreateSubdirectory("runtime-output");
        _deployment.Result = CreateResult(output, ready: true);

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<RuntimePrepareCommand>(),
            [
                "--version", "2.2.0",
                "--arch", "x64",
                "--output", output.FullName,
                "--json",
            ]);

        Assert.AreEqual(0, exitCode);
        using var json = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual("framework-dependent", json.RootElement.GetProperty("deploymentMode").GetString());
        Assert.AreEqual("2.2.0", json.RootElement.GetProperty("version").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.AreEqual(string.Empty, ConsoleStdErr.ToString());
    }

    [TestMethod]
    public async Task MissingFrameworkRuntimeReturnsTwoWithGuidanceJson()
    {
        var output = _tempDirectory.CreateSubdirectory("runtime-output");
        _deployment.Result = CreateResult(output, ready: false);

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<RuntimePrepareCommand>(),
            [
                "--version", "2.2.0",
                "--arch", "arm64",
                "--output", output.FullName,
                "--json",
            ]);

        Assert.AreEqual(2, exitCode);
        using var json = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.IsFalse(json.RootElement.GetProperty("ready").GetBoolean());
        StringAssert.Contains(json.RootElement.GetProperty("guidance").GetString(), "--install");
    }

    [TestMethod]
    public async Task ServiceFailureUsesJsonErrorEnvelope()
    {
        var output = _tempDirectory.CreateSubdirectory("runtime-output");
        _deployment.Exception = new InvalidOperationException("runtime payload unavailable");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<RuntimePrepareCommand>(),
            [
                "--version", "2.2.0",
                "--arch", "x64",
                "--output", output.FullName,
                "--json",
            ]);

        Assert.AreEqual(1, exitCode);
        using var json = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual("runtime payload unavailable", json.RootElement.GetProperty("error").GetString());
    }

    private static WindowsAppRuntimePrepareResult CreateResult(
        DirectoryInfo output,
        bool ready) =>
        new()
        {
            DeploymentMode = "framework-dependent",
            Version = "2.2.0",
            RuntimeVersion = "2.2.0",
            Architecture = "x64",
            OutputPath = output.FullName,
            BootstrapDllPath = Path.Combine(output.FullName, "Microsoft.WindowsAppRuntime.Bootstrap.dll"),
            Ready = ready,
            RuntimeRegistered = ready,
            Installed = false,
            InstalledPackageCount = 0,
            RuntimePackages =
            [
                new WindowsAppRuntimePackageIdentity("Microsoft.WindowsAppRuntime.2", "2.2.0.0"),
            ],
            Guidance = ready ? null : "Install with --install",
        };

    private sealed class FakeRuntimeDeploymentService : IWindowsAppRuntimeDeploymentService
    {
        public WindowsAppRuntimePrepareResult Result { get; set; } = null!;
        public Exception? Exception { get; set; }

        public Task<WindowsAppRuntimePrepareResult> PrepareAsync(
            string version,
            string architecture,
            DirectoryInfo outputDirectory,
            bool install,
            TaskContext taskContext,
            CancellationToken cancellationToken)
        {
            return Exception is null
                ? Task.FromResult(Result)
                : Task.FromException<WindowsAppRuntimePrepareResult>(Exception);
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class PowerShellServiceTests() : BaseCommandTests(configPaths: false)
{
    [TestMethod]
    public async Task RunCommandAsync_WithRestoreStyleStdOut_ShouldReturnStdOut()
    {
        var service = GetRequiredService<IPowerShellService>();

        var (exitCode, output, error) = await service.RunCommandAsync(
            "Write-Output 'SKIP|Microsoft.WindowsAppRuntime.1.8.msix|Already installed'; Write-Output 'INSTALLING|2 packages will be installed'",
            TestTaskContext,
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(
            output.Contains("SKIP|Microsoft.WindowsAppRuntime.1.8.msix|Already installed", StringComparison.Ordinal),
            $"Expected SKIP marker in output. Captured output:\n{output}");
        Assert.IsTrue(
            output.Contains("INSTALLING|2 packages will be installed", StringComparison.Ordinal),
            $"Expected INSTALLING marker in output. Captured output:\n{output}");
        Assert.IsTrue(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public async Task RunCommandAsync_WithRestoreStyleStdErr_ShouldReturnStdErr()
    {
        var service = GetRequiredService<IPowerShellService>();

        var (exitCode, output, error) = await service.RunCommandAsync(
            "[Console]::Error.WriteLine('ERROR|Microsoft.WindowsAppRuntime.1.8.msix|Installation failed'); exit 1",
            TestTaskContext,
            cancellationToken: TestContext.CancellationToken);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsTrue(string.IsNullOrWhiteSpace(output));
        Assert.IsTrue(
            error.Contains("ERROR|Microsoft.WindowsAppRuntime.1.8.msix|Installation failed", StringComparison.Ordinal),
            $"Expected ERROR marker in stderr. Captured error:\n{error}");
    }
}

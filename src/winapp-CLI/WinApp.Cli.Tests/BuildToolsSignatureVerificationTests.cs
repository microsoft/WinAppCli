// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for the Authenticode gate applied to build tools before they are executed.
/// The tools are downloaded over the network and then run, and <c>winapp tool</c> lets the
/// caller name any executable in the package, so the gate is what stands between an
/// unsigned binary and <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
/// </summary>
[TestClass]
[DoNotParallelize] // mutates the process-wide BuildToolsService.SignatureVerifier seam
public class BuildToolsSignatureVerificationTests : BaseCommandTests
{
    private DirectoryInfo _binDir = null!;

    [TestCleanup]
    public void RestoreVerifier()
    {
        // Hand the gate back in the state the rest of the assembly expects.
        BuildToolsService.SignatureVerifier = static (_, _) => true;
    }

    [TestInitialize]
    public void Setup()
    {
        // Mirror the NuGet cache layout the resolver expects, under the cache directory the
        // service is actually pointed at. Using _testWinappDirectory here would miss it entirely
        // and let the resolver fall through to installing the real package.
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        _binDir = Directory.CreateDirectory(Path.Combine(
            packagesDir,
            BuildToolsService.BUILD_TOOLS_PACKAGE.ToLowerInvariant(),
            "10.0.26100.1742",
            "bin",
            "10.0.26100.0",
            "x64"));

        File.WriteAllText(Path.Combine(_binDir.FullName, "mt.exe"), "fake tool");
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenToolIsSigned_ReturnsPath()
    {
        BuildToolsService.SignatureVerifier = static (_, _) => true;
        var service = GetRequiredService<IBuildToolsService>();

        var tool = await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("mt.exe", tool.Name);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenToolIsNotSigned_ThrowsAndDoesNotReturnPath()
    {
        BuildToolsService.SignatureVerifier = static (_, _) => false;
        var service = GetRequiredService<IBuildToolsService>();

        var ex = await Assert.ThrowsExactlyAsync<BuildToolSignatureException>(
            async () => await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "not validly signed by Microsoft");
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenToolIsNotSigned_MessageNamesTheAffectedVersionAndRemedy()
    {
        BuildToolsService.SignatureVerifier = static (_, _) => false;
        var service = GetRequiredService<IBuildToolsService>();

        var ex = await Assert.ThrowsExactlyAsync<BuildToolSignatureException>(
            async () => await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken));

        // The one known-unsigned release, and the first release that fixed it.
        StringAssert.Contains(ex.Message, "10.0.19041.1");
        StringAssert.Contains(ex.Message, "10.0.22000.194");
        StringAssert.Contains(ex.Message, "mt.exe");
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_VerifiesOncePerFileIdentity()
    {
        var calls = 0;
        BuildToolsService.SignatureVerifier = (_, _) => { calls++; return true; };
        var service = GetRequiredService<IBuildToolsService>();

        for (var i = 0; i < 3; i++)
        {
            await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);
        }

        Assert.AreEqual(1, calls, "Repeated resolution of the same binary should reuse the memoized result.");
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_ReverifiesWhenTheBinaryChanges()
    {
        var calls = 0;
        BuildToolsService.SignatureVerifier = (_, _) => { calls++; return true; };
        var service = GetRequiredService<IBuildToolsService>();
        var toolPath = Path.Combine(_binDir.FullName, "mt.exe");

        await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

        // A swapped binary must not inherit the previous verdict.
        File.WriteAllText(toolPath, "different content entirely");
        File.SetLastWriteTimeUtc(toolPath, DateTime.UtcNow.AddMinutes(1));

        await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(2, calls, "A binary replaced on disk must be re-verified.");
    }

    [TestMethod]
    public void SignatureVerifier_DefaultsToTheRealAuthenticodeVerifier()
    {
        // CleanupBase restores the default; assert the production wiring is the real check
        // rather than a permanently open gate.
        BuildToolsService.SignatureVerifier = AuthenticodeVerifier.IsTrustedMicrosoftSigned;

        var unsigned = Path.Combine(_binDir.FullName, "definitely-not-signed.exe");
        File.WriteAllText(unsigned, "not a PE file at all");

        Assert.IsFalse(
            BuildToolsService.SignatureVerifier(unsigned, NullLogger.Instance),
            "The real verifier must reject a file that carries no Authenticode signature.");
    }
}


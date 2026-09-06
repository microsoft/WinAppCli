// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class NativeAotVerifierTests
{
    private static readonly byte[] DotNetBundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];
   private static readonly string CmdPath = Path.GetFullPath("cmd.exe", Environment.SystemDirectory);
    private static readonly string[] ExpectedForbiddenPayloadFiles =
      ["coreclr.dll", "clrjit.dll", "App.dll", "App.runtimeconfig.json"];
   private DirectoryInfo _tempDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Directory.CreateDirectory(
            Path.GetFullPath(
                $"NativeAotVerifierTests_{Guid.NewGuid():N}",
                Path.GetTempPath()));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDirectory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort for a process that is still releasing its image file.
        }
    }

    [TestMethod]
    public void VerifyPayload_AcceptsNativeLayoutAndDepsJson()
    {
        var executable = WriteFile("App.exe", "native fixture");
        WriteFile("App.deps.json", "{}");
        WriteFile("helper.dll", "native helper");
        var verifier = new NativeAotVerifier(new FakePackageRegistrationService());

        var result = verifier.VerifyPayload(_tempDirectory, executable);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, result.ForbiddenFiles.Count);
    }

    [TestMethod]
    public void VerifyPayload_RejectsRuntimeAndManagedAppArtifacts()
    {
        var executable = WriteFile("App.exe", "apphost fixture");
        WriteFile("coreclr.dll", "runtime");
        WriteFile("clrjit.dll", "runtime");
        WriteFile("App.dll", "managed app");
        WriteFile("App.runtimeconfig.json", "{}");
        var verifier = new NativeAotVerifier(new FakePackageRegistrationService());

        var result = verifier.VerifyPayload(_tempDirectory, executable);

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEquivalent(
           ExpectedForbiddenPayloadFiles,
            result.ForbiddenFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void VerifyPayload_ExcludesTheExistingPackagedStagingDirectory()
    {
       var executable = WriteFile("App.exe", "native fixture");
       var stagingDirectory = _tempDirectory.CreateSubdirectory("AppX");
       File.WriteAllText(
           Path.GetFullPath("coreclr.dll", stagingDirectory.FullName),
           "stale staging file");
       var verifier = new NativeAotVerifier(new FakePackageRegistrationService());

       var result = verifier.VerifyPayload(_tempDirectory, executable, stagingDirectory);

       Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void VerifyPayload_RejectsSingleFileJitBundleWithoutRuntimeSidecars()
    {
       var executable = new FileInfo(
           Path.GetFullPath("App.exe", _tempDirectory.FullName));
       using (var stream = executable.Create())
       {
           stream.SetLength(256);
           stream.Position = 64;
           stream.Write(BitConverter.GetBytes(160L));
           stream.Write(DotNetBundleSignature);
       }
       var verifier = new NativeAotVerifier(new FakePackageRegistrationService());

       var result = verifier.VerifyPayload(_tempDirectory, executable);

       Assert.IsFalse(result.Succeeded);
       StringAssert.Contains(result.Error, "single-file bundle");
       StringAssert.Contains(result.Error, "cannot be certified as Native AOT");
    }

    [TestMethod]
    public async Task VerifyRuntime_UnpackagedNativeProcessChecksModulesAndExactPath()
    {
        var verifier = new NativeAotVerifier(new FakePackageRegistrationService());
        using var process = StartLongRunningCommand(CmdPath);
        var stopwatch = Stopwatch.StartNew();
        try
        {
           var expected = CmdPath;
            var result = await verifier.VerifyRuntimeAsync(
                new NativeAotRuntimeVerificationRequest(
                    unchecked((uint)process.Id),
                    expected,
                    expected,
                    ProjectPackaging.Unpackaged),
                CancellationToken.None);

            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.IsTrue(result.Alive);
            Assert.IsTrue(result.RuntimeModules);
            Assert.IsTrue(result.ProcessProvenance);
            Assert.IsFalse(result.LoadedModules.Any(module =>
                module.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(
                stopwatch.Elapsed >= TimeSpan.FromSeconds(3),
                "Verification must observe process liveness for the full startup window.");
        }
        finally
        {
            TryKill(process);
        }
    }

    [TestMethod]
    public async Task VerifyRuntime_PackagedRequiresMatchingDevelopmentRegistrationAndPayload()
    {
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        var stagingDirectory = _tempDirectory.CreateSubdirectory("stage");
        var source = Path.GetFullPath("App.exe", publishDirectory.FullName);
        var staged = Path.GetFullPath("App.exe", stagingDirectory.FullName);
        File.Copy(CmdPath, source);
        File.Copy(source, staged);

        var packages = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new DevPackageInfo(
                    "Contoso.App_1.0.0.0_x64__test",
                    "Contoso.App",
                    "1.0.0.0",
                    stagingDirectory.FullName,
                    IsDevelopmentMode: true),
            ],
        };
        var verifier = new NativeAotVerifier(packages);
        using var process = StartLongRunningCommand(staged);
        try
        {
            var result = await verifier.VerifyRuntimeAsync(
                new NativeAotRuntimeVerificationRequest(
                    unchecked((uint)process.Id),
                    source,
                    staged,
                    ProjectPackaging.Packaged,
                    stagingDirectory.FullName,
                    "Contoso.App"),
                CancellationToken.None);

            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(true, result.PackageRegistration);
            Assert.IsTrue(result.ProcessProvenance);
        }
        finally
        {
            TryKill(process);
        }
    }

    [TestMethod]
    public async Task VerifyRuntime_ProcessThatExitsDuringReadinessWindowFails()
    {
        var verifier = new NativeAotVerifier(new FakePackageRegistrationService());
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = "/d /c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var result = await verifier.VerifyRuntimeAsync(
            new NativeAotRuntimeVerificationRequest(
                unchecked((uint)process.Id),
                CmdPath,
                CmdPath,
                ProjectPackaging.Unpackaged,
                ExitCodeProvider: () => process.ExitCode),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Alive);
        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error, "exited with exit code 0 before");
        StringAssert.Contains(result.Error, "--debug-output");
        StringAssert.Contains(result.Error, "--symbols");
    }

    private static Process StartLongRunningCommand(string executable) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "/d /c ping -n 30 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private FileInfo WriteFile(string relativePath, string contents)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException(
                $"Fixture path must be relative: '{relativePath}'.",
                nameof(relativePath));
        }

        var path = Path.GetFullPath(relativePath, _tempDirectory.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return new FileInfo(path);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}

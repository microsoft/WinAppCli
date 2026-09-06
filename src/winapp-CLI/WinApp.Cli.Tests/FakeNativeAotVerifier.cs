// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

internal sealed class FakeNativeAotVerifier : INativeAotVerifier
{
    public NativeAotStaticVerification StaticResult { get; set; } =
        new(true, []);

    public NativeAotRuntimeVerification RuntimeResult { get; set; } =
        new(
            Succeeded: true,
            Alive: true,
            RuntimeModules: true,
            ProcessProvenance: true,
            PackageRegistration: null,
            ProcessPath: @"C:\App\App.exe",
            LoadedModules: ["App.exe", "kernel32.dll"],
            MainWindowHandle: 42,
            MainWindowTitle: "App");

    public List<(string PublishDirectory, string SourceExecutable)> StaticCalls { get; } = [];
    public List<NativeAotRuntimeVerificationRequest> RuntimeCalls { get; } = [];

    public NativeAotStaticVerification VerifyPayload(
        DirectoryInfo publishDirectory,
       FileInfo sourceExecutable,
       DirectoryInfo? excludedStagingDirectory = null)
    {
        StaticCalls.Add((publishDirectory.FullName, sourceExecutable.FullName));
        return StaticResult;
    }

    public Task<NativeAotRuntimeVerification> VerifyRuntimeAsync(
        NativeAotRuntimeVerificationRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeCalls.Add(request);
        return Task.FromResult(RuntimeResult with
        {
            ProcessPath = RuntimeResult.ProcessPath == @"C:\App\App.exe"
                ? request.ExpectedProcessPath
                : RuntimeResult.ProcessPath,
        });
    }
}

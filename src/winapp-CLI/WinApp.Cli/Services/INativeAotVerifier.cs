// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface INativeAotVerifier
{
    NativeAotStaticVerification VerifyPayload(
        DirectoryInfo publishDirectory,
        FileInfo sourceExecutable,
        DirectoryInfo? excludedStagingDirectory = null);

    Task<NativeAotRuntimeVerification> VerifyRuntimeAsync(
        NativeAotRuntimeVerificationRequest request,
        CancellationToken cancellationToken);
}

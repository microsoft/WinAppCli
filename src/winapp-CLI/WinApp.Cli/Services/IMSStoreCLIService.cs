// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IMSStoreCLIService
{
    Task EnsureMSStoreCLIAvailableAsync(CancellationToken cancellationToken = default);
    string GetMSStoreCLIPath();
}

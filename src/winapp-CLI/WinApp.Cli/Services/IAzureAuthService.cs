// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Provides Azure authentication tokens for ARM and Trusted Signing data plane operations.
/// </summary>
internal interface IAzureAuthService
{
    /// <summary>
    /// Acquires an access token for the specified resource scope.
    /// Tries environment credentials, workload identity, managed identity, and then Azure CLI.
    /// </summary>
    /// <param name="scope">The OAuth2 scope (e.g., "https://management.azure.com/.default")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A valid bearer token</returns>
    /// <exception cref="InvalidOperationException">When authentication fails in a non-interactive environment</exception>
    Task<string> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the current environment supports interactive authentication.
    /// </summary>
    bool IsInteractive { get; }

    /// <summary>
    /// The Azure tenant ID used for authentication, if known.
    /// Set from AZURE_TENANT_ID environment variable or user input during interactive login.
    /// </summary>
    string? TenantId { get; }
}

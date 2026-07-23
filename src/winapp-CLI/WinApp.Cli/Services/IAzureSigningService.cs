// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Provides access to Azure Trusted Signing REST APIs for discovering
/// subscriptions, signing accounts, and certificate profiles.
/// </summary>
internal interface IAzureSigningService
{
    /// <summary>
    /// Lists all Azure subscriptions accessible to the authenticated user.
    /// </summary>
    Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists Trusted Signing accounts. If resourceGroup is provided, lists only within that group.
    /// Otherwise lists all signing accounts in the subscription.
    /// </summary>
    Task<IReadOnlyList<SigningAccount>> ListSigningAccountsAsync(string accessToken, string subscriptionId, string? resourceGroup = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single signing account by name via a direct ARM GET, avoiding the broader list
    /// permission that <see cref="ListSigningAccountsAsync"/> requires. Returns null if the
    /// account is not found (404).
    /// </summary>
    Task<SigningAccount?> GetSigningAccountAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists certificate profiles under a signing account.
    /// </summary>
    Task<IReadOnlyList<CertificateProfile>> ListCertificateProfilesAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default);
}

internal record AzureSubscription(string SubscriptionId, string DisplayName);

internal record SigningAccount(string Name, string ResourceGroup, string Location, string? AccountUri);

internal record CertificateProfile(string Name, string ProfileType, string Status);

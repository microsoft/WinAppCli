// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

/// <summary>
/// Calls Azure REST APIs to discover Trusted Signing resources.
/// NativeAOT-compatible — uses raw HTTP with System.Text.Json.
/// </summary>
internal class AzureSigningService : IAzureSigningService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string ArmBaseUrl = "https://management.azure.com";
    private const string SubscriptionsApiVersion = "2022-12-01";
    private const string TrustedSigningApiVersion = "2024-02-05-preview";

    private readonly ILogger<AzureSigningService> logger;
    private readonly HttpClient http;

    public AzureSigningService(ILogger<AzureSigningService> logger) : this(logger, SharedHttp)
    {
    }

    // Test seam: allows injecting an HttpClient backed by a stub message handler.
    internal AzureSigningService(ILogger<AzureSigningService> logger, HttpClient httpClient)
    {
        this.logger = logger;
        this.http = httpClient;
    }

    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var url = $"{ArmBaseUrl}/subscriptions?api-version={SubscriptionsApiVersion}";
        var json = await GetArmResponseAsync(url, accessToken, cancellationToken);

        var subscriptions = new List<AzureSubscription>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            return subscriptions;
        }

        foreach (var item in valueArray.EnumerateArray())
        {
            var subId = item.GetProperty("subscriptionId").GetString() ?? "";
            var displayName = item.GetProperty("displayName").GetString() ?? "";
            var state = item.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;

            // Only include enabled subscriptions
            if (string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase))
            {
                subscriptions.Add(new AzureSubscription(subId, displayName));
            }
        }

        logger.LogInformation("Found {Count} enabled subscription(s)", subscriptions.Count);
        return subscriptions;
    }

    public async Task<IReadOnlyList<SigningAccount>> ListSigningAccountsAsync(string accessToken, string subscriptionId, string? resourceGroup = null, CancellationToken cancellationToken = default)
    {
        string url;
        if (!string.IsNullOrEmpty(resourceGroup))
        {
            url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CodeSigning/codeSigningAccounts?api-version={TrustedSigningApiVersion}";
        }
        else
        {
            url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/providers/Microsoft.CodeSigning/codeSigningAccounts?api-version={TrustedSigningApiVersion}";
        }

        var json = await GetArmResponseAsync(url, accessToken, cancellationToken);

        var accounts = new List<SigningAccount>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            return accounts;
        }

        foreach (var item in valueArray.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var location = item.TryGetProperty("location", out var locProp) ? locProp.GetString() ?? "" : "";
            var id = item.GetProperty("id").GetString() ?? "";

            // Parse resource group from the resource ID
            var rg = ParseResourceGroupFromId(id);

            // Get account URI from properties
            string? accountUri = null;
            if (item.TryGetProperty("properties", out var props) &&
                props.TryGetProperty("accountUri", out var uriProp))
            {
                accountUri = uriProp.GetString();
            }

            accounts.Add(new SigningAccount(name, rg, location, accountUri));
        }

        logger.LogInformation("Found {Count} signing account(s)", accounts.Count);
        return accounts;
    }

    public async Task<IReadOnlyList<CertificateProfile>> ListCertificateProfilesAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default)
    {
        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CodeSigning/codeSigningAccounts/{accountName}/certificateProfiles?api-version={TrustedSigningApiVersion}";

        var json = await GetArmResponseAsync(url, accessToken, cancellationToken);

        var profiles = new List<CertificateProfile>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            return profiles;
        }

        foreach (var item in valueArray.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var profileType = "";
            var status = "";

            if (item.TryGetProperty("properties", out var props))
            {
                profileType = props.TryGetProperty("profileType", out var typeProp) ? typeProp.GetString() ?? "" : "";
                status = props.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
            }

            profiles.Add(new CertificateProfile(name, profileType, status));
        }

        logger.LogInformation("Found {Count} certificate profile(s) for account '{Account}'", profiles.Count, accountName);
        return profiles;
    }

    private async Task<string> GetArmResponseAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = TryParseAzureError(content) ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            throw new InvalidOperationException($"Azure API request failed: {errorMessage}");
        }

        return content;
    }

    private static string? TryParseAzureError(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                var code = errorObj.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
                var message = errorObj.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
                if (message != null)
                {
                    return code != null ? $"{code}: {message}" : message;
                }
            }
        }
        catch
        {
            // Not valid JSON or unexpected structure
        }
        return null;
    }

    private static string ParseResourceGroupFromId(string resourceId)
    {
        // Resource ID format: /subscriptions/{sub}/resourceGroups/{rg}/providers/...
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }
        return "";
    }
}

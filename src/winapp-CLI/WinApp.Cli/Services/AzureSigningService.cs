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
    private static readonly string ArmHost = new Uri(ArmBaseUrl).Host;
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
        var subscriptions = await GetArmPagedAsync<AzureSubscription>(url, accessToken, item =>
        {
            var state = item.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;

            // Only include enabled subscriptions
            if (!string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var subId = item.GetProperty("subscriptionId").GetString() ?? "";
            var displayName = item.GetProperty("displayName").GetString() ?? "";
            return new AzureSubscription(subId, displayName);
        }, cancellationToken);

        logger.LogInformation("Found {Count} enabled subscription(s)", subscriptions.Count);
        return subscriptions;
    }

    public async Task<IReadOnlyList<SigningAccount>> ListSigningAccountsAsync(string accessToken, string subscriptionId, string? resourceGroup = null, CancellationToken cancellationToken = default)
    {
        string url = !string.IsNullOrEmpty(resourceGroup)
            ? $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CodeSigning/codeSigningAccounts?api-version={TrustedSigningApiVersion}"
            : $"{ArmBaseUrl}/subscriptions/{subscriptionId}/providers/Microsoft.CodeSigning/codeSigningAccounts?api-version={TrustedSigningApiVersion}";

        var accounts = await GetArmPagedAsync<SigningAccount>(url, accessToken, item =>
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

            return new SigningAccount(name, rg, location, accountUri);
        }, cancellationToken);

        logger.LogInformation("Found {Count} signing account(s)", accounts.Count);
        return accounts;
    }

    public async Task<IReadOnlyList<CertificateProfile>> ListCertificateProfilesAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default)
    {
        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CodeSigning/codeSigningAccounts/{accountName}/certificateProfiles?api-version={TrustedSigningApiVersion}";

        var profiles = await GetArmPagedAsync<CertificateProfile>(url, accessToken, item =>
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var profileType = "";
            var status = "";

            if (item.TryGetProperty("properties", out var props))
            {
                profileType = props.TryGetProperty("profileType", out var typeProp) ? typeProp.GetString() ?? "" : "";
                status = props.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
            }

            return new CertificateProfile(name, profileType, status);
        }, cancellationToken);

        logger.LogInformation("Found {Count} certificate profile(s) for account '{Account}'", profiles.Count, accountName);
        return profiles;
    }

    public async Task<SigningAccount?> GetSigningAccountAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default)
    {
        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CodeSigning/codeSigningAccounts/{accountName}?api-version={TrustedSigningApiVersion}";

        var item = await GetArmResourceAsync(url, accessToken, cancellationToken);
        if (item == null)
        {
            return null;
        }

        var element = item.Value;
        var name = element.GetProperty("name").GetString() ?? "";
        var location = element.TryGetProperty("location", out var locProp) ? locProp.GetString() ?? "" : "";
        var id = element.GetProperty("id").GetString() ?? "";
        var rg = ParseResourceGroupFromId(id);

        string? accountUri = null;
        if (element.TryGetProperty("properties", out var props) &&
            props.TryGetProperty("accountUri", out var uriProp))
        {
            accountUri = uriProp.GetString();
        }

        return new SigningAccount(name, rg, location, accountUri);
    }

    public async Task<CertificateProfile?> GetCertificateProfileAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, string profileName, CancellationToken cancellationToken = default)
    {
        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CodeSigning/codeSigningAccounts/{accountName}/certificateProfiles/{profileName}?api-version={TrustedSigningApiVersion}";

        var item = await GetArmResourceAsync(url, accessToken, cancellationToken);
        if (item == null)
        {
            return null;
        }

        var element = item.Value;
        var name = element.GetProperty("name").GetString() ?? "";
        var profileType = "";
        var status = "";
        if (element.TryGetProperty("properties", out var props))
        {
            profileType = props.TryGetProperty("profileType", out var typeProp) ? typeProp.GetString() ?? "" : "";
            status = props.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
        }

        return new CertificateProfile(name, profileType, status);
    }

    /// <summary>
    /// Issues a single ARM GET and returns the parsed root element, or null on 404. Shared by the
    /// direct-GET resource lookups (<see cref="GetSigningAccountAsync"/>,
    /// <see cref="GetCertificateProfileAsync"/>) so they need only the reader role on the specific
    /// resource rather than list permission on the parent collection.
    /// </summary>
    private async Task<JsonElement?> GetArmResourceAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = TryParseAzureError(content) ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            throw new InvalidOperationException($"Azure API request failed: {errorMessage}");
        }

        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Fetches an ARM list endpoint and follows the <c>nextLink</c> continuation until it is
    /// absent, so results that span multiple pages (many subscriptions/accounts/profiles) are
    /// returned in full. <paramref name="selector"/> may return <c>null</c> to skip an item.
    /// </summary>
    private async Task<IReadOnlyList<T>> GetArmPagedAsync<T>(
        string url,
        string accessToken,
        Func<JsonElement, T?> selector,
        CancellationToken cancellationToken)
        where T : class
    {
        var results = new List<T>();

        // Track visited links with ordinal (case-sensitive) comparison. ARM's nextLink carries an
        // opaque continuation token in its query string that may be case-sensitive, so two genuinely
        // distinct pages can differ only by case; a case-insensitive set would treat them as a repeat
        // and abort pagination early, silently dropping results. Host trust is validated separately by
        // EnsureTrustedArmUrl, so this comparison only needs to detect exact-URL cycles.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? nextUrl = url;

        while (!string.IsNullOrEmpty(nextUrl))
        {
            // Guard against a cyclic or repeated nextLink so a misbehaving service can't spin
            // this loop forever.
            if (!visited.Add(nextUrl))
            {
                throw new InvalidOperationException(
                    "Azure API pagination returned a repeated nextLink; aborting to avoid an infinite loop.");
            }

            // Never attach the bearer token to a URL the server can choose: only follow HTTPS
            // links that stay on the ARM host. A crafted nextLink pointing elsewhere would
            // otherwise leak the access token to an attacker-controlled endpoint.
            EnsureTrustedArmUrl(nextUrl);

            var json = await GetArmResponseAsync(nextUrl, accessToken, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("value", out var valueArray)
                && valueArray.ValueKind == JsonValueKind.Array)
            {
                results.AddRange(valueArray.EnumerateArray()
                    .Select(selector)
                    .Where(selected => selected != null)
                    .Select(selected => selected!));
            }

            nextUrl = doc.RootElement.TryGetProperty("nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }

        return results;
    }

    /// <summary>
    /// Rejects a pagination URL that is not an absolute HTTPS URL on the Azure Resource Manager
    /// host, so the bearer token is never sent to a server-chosen or attacker-controlled endpoint.
    /// </summary>
    private static void EnsureTrustedArmUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, ArmHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to follow an untrusted Azure pagination link '{url}'. " +
                $"Expected an https URL on host '{ArmHost}'.");
        }
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
        catch (JsonException)
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

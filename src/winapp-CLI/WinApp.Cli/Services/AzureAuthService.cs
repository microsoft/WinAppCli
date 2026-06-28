// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

/// <summary>
/// Provides Azure authentication via environment variables, workload identity,
/// managed identity, Azure CLI, or device code flow.
/// NativeAOT-compatible — does not depend on Azure.Identity.
/// </summary>
internal class AzureAuthService(ILogger<AzureAuthService> logger) : IAzureAuthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Azure CLI's well-known first-party client ID — commonly reused by developer tools
    private const string DeviceCodeClientId = "04b07795-a710-4532-b716-798fc87e2379";
    private const string AuthorityBase = "https://login.microsoftonline.com";

    public bool IsInteractive =>
        Environment.UserInteractive
        && !Console.IsInputRedirected
        && Environment.GetEnvironmentVariable("CI") == null
        && Environment.GetEnvironmentVariable("TF_BUILD") == null
        && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == null;

    public async Task<string> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        // 1. Try environment variable credentials (service principal with client secret)
        var envToken = await TryEnvironmentCredentialAsync(scope, cancellationToken);
        if (envToken != null)
        {
            logger.LogInformation("Authenticated via environment credentials (service principal)");
            return envToken;
        }

        // 2. Try workload identity (OIDC federation — GitHub Actions, AKS, etc.)
        var workloadToken = await TryWorkloadIdentityAsync(scope, cancellationToken);
        if (workloadToken != null)
        {
            logger.LogInformation("Authenticated via workload identity (OIDC federation)");
            return workloadToken;
        }

        // 3. Try managed identity (Azure VMs, App Service, Container Apps)
        var managedToken = await TryManagedIdentityAsync(scope, cancellationToken);
        if (managedToken != null)
        {
            logger.LogInformation("Authenticated via managed identity");
            return managedToken;
        }

        // 4. Try Azure CLI token
        var cliToken = await TryAzureCliTokenAsync(scope, cancellationToken);
        if (cliToken != null)
        {
            logger.LogInformation("Authenticated via Azure CLI");
            return cliToken;
        }

        // 5. Fall back to device code flow (interactive only)
        if (!IsInteractive)
        {
            throw new InvalidOperationException(
                "Azure authentication failed. No credentials found in the environment.\n\n" +
                "For CI/CD, set these environment variables:\n" +
                "  AZURE_TENANT_ID     - Your Azure AD tenant ID\n" +
                "  AZURE_CLIENT_ID     - Service principal application ID\n" +
                "  AZURE_CLIENT_SECRET - Service principal secret\n\n" +
                "For GitHub Actions OIDC, set:\n" +
                "  AZURE_TENANT_ID, AZURE_CLIENT_ID, and AZURE_FEDERATED_TOKEN_FILE\n\n" +
                "For managed identity (Azure-hosted runners), no configuration is needed.\n\n" +
                "Alternatively, ensure the Azure CLI is installed and run 'az login' before this command.");
        }

        logger.LogDebug("Attempting device code flow for interactive authentication");
        return await DeviceCodeFlowAsync(scope, cancellationToken);
    }

    private static async Task<string?> TryEnvironmentCredentialAsync(string scope, CancellationToken cancellationToken)
    {
        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return null;
        }

        var tokenEndpoint = $"{AuthorityBase}/{tenantId}/oauth2/v2.0/token";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        });

        var response = await Http.PostAsync(tokenEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    private static async Task<string?> TryWorkloadIdentityAsync(string scope, CancellationToken cancellationToken)
    {
        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var tokenFilePath = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");

        // GitHub Actions OIDC uses a different mechanism — request token from the runtime
        var actionsTokenUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var actionsTokenBearer = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        string? federatedToken = null;

        // Path 1: Token file (AKS workload identity, generic OIDC)
        if (!string.IsNullOrEmpty(tokenFilePath) && File.Exists(tokenFilePath))
        {
            federatedToken = (await File.ReadAllTextAsync(tokenFilePath, cancellationToken)).Trim();
        }
        // Path 2: GitHub Actions OIDC runtime
        else if (!string.IsNullOrEmpty(actionsTokenUrl) && !string.IsNullOrEmpty(actionsTokenBearer))
        {
            federatedToken = await RequestGitHubActionsOidcTokenAsync(actionsTokenUrl, actionsTokenBearer, scope, cancellationToken);
        }

        if (string.IsNullOrEmpty(federatedToken))
        {
            return null;
        }

        // Exchange the federated token for an Azure access token
        var tokenEndpoint = $"{AuthorityBase}/{tenantId}/oauth2/v2.0/token";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = federatedToken,
            ["scope"] = scope
        });

        var response = await Http.PostAsync(tokenEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    private static async Task<string?> RequestGitHubActionsOidcTokenAsync(
        string requestUrl, string requestToken, string scope, CancellationToken cancellationToken)
    {
        try
        {
            // Extract audience from scope (e.g., "https://management.azure.com/.default" → "api://AzureADTokenExchange")
            var audience = "api://AzureADTokenExchange";
            var url = $"{requestUrl}&audience={Uri.EscapeDataString(audience)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);

            var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryManagedIdentityAsync(string scope, CancellationToken cancellationToken)
    {
        // Extract resource from scope (remove /.default suffix for the IMDS/App Service endpoints)
        var resource = scope.EndsWith("/.default", StringComparison.Ordinal)
            ? scope[..^"/.default".Length]
            : scope;

        // Path 1: App Service / Container Apps managed identity
        var identityEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var identityHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");

        if (!string.IsNullOrEmpty(identityEndpoint) && !string.IsNullOrEmpty(identityHeader))
        {
            return await TryAppServiceManagedIdentityAsync(identityEndpoint, identityHeader, resource, cancellationToken);
        }

        // Path 2: IMDS (Azure VMs, VMSS)
        return await TryImdsManagedIdentityAsync(resource, cancellationToken);
    }

    private static async Task<string?> TryAppServiceManagedIdentityAsync(
        string endpoint, string identityHeader, string resource, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{endpoint}?api-version=2019-08-01&resource={Uri.EscapeDataString(resource)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-IDENTITY-HEADER", identityHeader);

            var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var tokenProp) ? tokenProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryImdsManagedIdentityAsync(string resource, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource={Uri.EscapeDataString(resource)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Metadata", "true");

            // Use a short timeout — IMDS is local and responds instantly if available.
            // A long hang means we're not on an Azure VM.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var tokenProp) ? tokenProp.GetString() : null;
        }
        catch
        {
            // Not on an Azure VM or IMDS not available
            return null;
        }
    }

    private static async Task<string?> TryAzureCliTokenAsync(string scope, CancellationToken cancellationToken)
    {
        try
        {
            var resource = scope.EndsWith("/.default", StringComparison.Ordinal)
                ? scope[..^"/.default".Length]
                : scope;

            // On Windows, 'az' is a .cmd file which requires cmd.exe to execute.
            // UseShellExecute=false won't resolve .cmd files, so we invoke via cmd /c.
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = $"/c az account get-access-token --resource {resource} --query accessToken -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var token = output.Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            // Azure CLI not installed or not in PATH
            return null;
        }
    }

    private static async Task<string?> TryGetTenantFromAzureCliAsync(CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = "/c az account show --query tenantId -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var tenantId = output.Trim();
            return string.IsNullOrEmpty(tenantId) ? null : tenantId;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> DeviceCodeFlowAsync(string scope, CancellationToken cancellationToken)
    {
        // Device code flow requires a specific tenant — /common/ and /organizations/ are blocked.
        // Try to discover the tenant from AZURE_TENANT_ID env var or Azure CLI.
        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");

        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = await TryGetTenantFromAzureCliAsync(cancellationToken);
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException(
                "Azure device code login requires a tenant ID.\n\n" +
                "Set the AZURE_TENANT_ID environment variable, or run 'az login' first.\n" +
                "You can find your tenant ID in the Azure Portal under Azure Active Directory > Overview.");
        }

        var deviceCodeEndpoint = $"{AuthorityBase}/{tenantId}/oauth2/v2.0/devicecode";
        var tokenEndpoint = $"{AuthorityBase}/{tenantId}/oauth2/v2.0/token";

        // Request device code
        var deviceContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = DeviceCodeClientId,
            ["scope"] = scope
        });

        var deviceResponse = await Http.PostAsync(deviceCodeEndpoint, deviceContent, cancellationToken);
        var deviceJson = await deviceResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!deviceResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to initiate device code authentication. Response: {deviceJson}");
        }

        using var deviceDoc = JsonDocument.Parse(deviceJson);
        var deviceCode = deviceDoc.RootElement.GetProperty("device_code").GetString()!;
        var userCode = deviceDoc.RootElement.GetProperty("user_code").GetString()!;
        var verificationUri = deviceDoc.RootElement.GetProperty("verification_uri").GetString()!;
        var interval = deviceDoc.RootElement.TryGetProperty("interval", out var intervalProp)
            ? intervalProp.GetInt32()
            : 5;
        var expiresIn = deviceDoc.RootElement.TryGetProperty("expires_in", out var expiresProp)
            ? expiresProp.GetInt32()
            : 900;

        // Display instructions to user
        Console.WriteLine();
        Console.WriteLine($"To sign in, use a web browser to open the page {verificationUri}");
        Console.WriteLine($"and enter the code: {userCode}");
        Console.WriteLine();

        // Poll for token
        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);

            var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = DeviceCodeClientId,
                ["device_code"] = deviceCode
            });

            var tokenResponse = await Http.PostAsync(tokenEndpoint, tokenContent, cancellationToken);
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);

            using var tokenDoc = JsonDocument.Parse(tokenJson);
            if (tokenResponse.IsSuccessStatusCode)
            {
                return tokenDoc.RootElement.GetProperty("access_token").GetString()!;
            }

            var error = tokenDoc.RootElement.TryGetProperty("error", out var errorProp)
                ? errorProp.GetString()
                : null;

            if (error == "authorization_pending")
            {
                continue;
            }

            if (error == "slow_down")
            {
                interval += 5;
                continue;
            }

            // Any other error is terminal
            var errorDescription = tokenDoc.RootElement.TryGetProperty("error_description", out var descProp)
                ? descProp.GetString()
                : "Unknown error";
            throw new InvalidOperationException($"Authentication failed: {errorDescription}");
        }

        throw new InvalidOperationException("Device code authentication timed out. Please try again.");
    }
}

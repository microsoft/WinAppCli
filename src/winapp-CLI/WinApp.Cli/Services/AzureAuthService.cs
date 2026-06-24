// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

/// <summary>
/// Provides Azure authentication via environment variables (CI), Azure CLI, or device code flow.
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
        // 1. Try environment variable credentials (service principal — for CI)
        var envToken = await TryEnvironmentCredentialAsync(scope, cancellationToken);
        if (envToken != null)
        {
            logger.LogDebug("Authenticated via environment credentials (service principal)");
            return envToken;
        }

        // 2. Try Azure CLI token
        var cliToken = await TryAzureCliTokenAsync(scope, cancellationToken);
        if (cliToken != null)
        {
            logger.LogDebug("Authenticated via Azure CLI");
            return cliToken;
        }

        // 3. Fall back to device code flow (interactive only)
        if (!IsInteractive)
        {
            throw new InvalidOperationException(
                "Azure authentication failed. No credentials found in the environment.\n\n" +
                "For CI/CD, set these environment variables:\n" +
                "  AZURE_TENANT_ID     - Your Azure AD tenant ID\n" +
                "  AZURE_CLIENT_ID     - Service principal application ID\n" +
                "  AZURE_CLIENT_SECRET - Service principal secret\n\n" +
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

    private static async Task<string?> TryAzureCliTokenAsync(string scope, CancellationToken cancellationToken)
    {
        try
        {
            // Extract resource from scope (remove /.default suffix)
            var resource = scope.EndsWith("/.default", StringComparison.Ordinal)
                ? scope[..^"/.default".Length]
                : scope;

            var psi = new ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"account get-access-token --resource {resource} --query accessToken -o tsv",
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

    private static async Task<string> DeviceCodeFlowAsync(string scope, CancellationToken cancellationToken)
    {
        var deviceCodeEndpoint = $"{AuthorityBase}/organizations/oauth2/v2.0/devicecode";
        var tokenEndpoint = $"{AuthorityBase}/organizations/oauth2/v2.0/token";

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

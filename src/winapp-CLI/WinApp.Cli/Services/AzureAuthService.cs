// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace WinApp.Cli.Services;

/// <summary>
/// Provides Azure authentication using Azure.Identity's credential chain.
/// In interactive environments, falls back to running 'az login' when
/// DefaultAzureCredential fails — the Trusted Signing dlib requires
/// AzureCliCredential for local interactive signing.
/// </summary>
internal class AzureAuthService(ILogger<AzureAuthService> logger, IAnsiConsole ansiConsole) : IAzureAuthService
{
    public bool IsInteractive =>
        Environment.UserInteractive
        && !Console.IsInputRedirected
        && Environment.GetEnvironmentVariable("CI") == null
        && Environment.GetEnvironmentVariable("TF_BUILD") == null
        && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == null;

    public string? TenantId { get; private set; } = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");

    public async Task<string> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var credential = CreateCredential();

        try
        {
            var context = new TokenRequestContext([scope]);
            var token = await credential.GetTokenAsync(context, cancellationToken);
            LogAuthMethod(token);
            return token.Token;
        }
        catch (AuthenticationFailedException)
        {
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

            // The Trusted Signing dlib requires AzureCliCredential for interactive signing.
            // Check if Azure CLI is available and run 'az login' for the user.
            var azPath = FindAzureCli();
            if (azPath == null)
            {
                throw new InvalidOperationException(
                    "Azure authentication failed. To sign interactively, you have two options:\n\n" +
                    "Option 1: Install the Azure CLI and run this command again (login will be handled automatically)\n" +
                    "  Install from: https://aka.ms/installazurecli\n\n" +
                    "Option 2: Set environment variables for a service principal:\n" +
                    "  AZURE_TENANT_ID     - Your Azure AD tenant ID\n" +
                    "  AZURE_CLIENT_ID     - Service principal application ID\n" +
                    "  AZURE_CLIENT_SECRET - Service principal secret");
            }

            var tenantId = TenantId;

            if (string.IsNullOrEmpty(tenantId))
            {
                tenantId = await ansiConsole.PromptAsync(
                    new TextPrompt<string>("Enter your [green]Azure Tenant ID[/] (found in Azure Portal > Azure AD > Overview):")
                        .ValidationErrorMessage("[red]Tenant ID cannot be empty[/]")
                        .Validate(input => !string.IsNullOrWhiteSpace(input)),
                    cancellationToken);
                TenantId = tenantId;
            }

            ansiConsole.MarkupLine("[yellow]Signing in via Azure CLI...[/]");
            var success = await RunAzLoginAsync(azPath, tenantId, cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException("Azure CLI login failed. Please try running 'az login' manually.");
            }

            // Retry with the now-valid Azure CLI credential
            var retryCredential = new AzureCliCredential();
            var retryToken = await retryCredential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            logger.LogInformation("Authenticated via Azure CLI");
            return retryToken.Token;
        }
    }

    private static DefaultAzureCredential CreateCredential()
    {
        var options = new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
        };

        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        if (!string.IsNullOrEmpty(tenantId))
        {
            options.TenantId = tenantId;
        }

        return new DefaultAzureCredential(options);
    }

    private static string? FindAzureCli()
    {
        // Check common install locations on Windows
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fall back to PATH lookup
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "az.cmd",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                if (p.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    return output.Split('\n')[0].Trim();
                }
            }
        }
        catch
        {
            // where.exe not available or failed
        }

        return null;
    }

    private static async Task<bool> RunAzLoginAsync(string azPath, string tenantId, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = azPath,
            Arguments = $"login --tenant {tenantId}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false // Allow browser interaction
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Azure CLI");
        await p.WaitForExitAsync(cancellationToken);
        return p.ExitCode == 0;
    }

    private void LogAuthMethod(AccessToken token)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET")))
        {
            logger.LogInformation("Authenticated via environment credentials (service principal)");
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL")))
        {
            logger.LogInformation("Authenticated via workload identity (OIDC federation)");
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")))
        {
            logger.LogInformation("Authenticated via managed identity");
        }
        else
        {
            logger.LogInformation("Authenticated via Azure CLI");
        }
    }
}

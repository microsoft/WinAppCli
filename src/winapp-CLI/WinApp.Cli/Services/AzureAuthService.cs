// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Telemetry;

namespace WinApp.Cli.Services;

/// <summary>
/// Provides Azure authentication using Azure.Identity's credential chain.
/// In interactive environments, falls back to running 'az login' when
/// DefaultAzureCredential fails — the Trusted Signing dlib requires
/// AzureCliCredential for local interactive signing.
/// </summary>
internal partial class AzureAuthService(ILogger<AzureAuthService> logger, IAnsiConsole ansiConsole) : IAzureAuthService
{
    public virtual bool IsInteractive =>
        Environment.UserInteractive
        && !Console.IsInputRedirected
        && !CIEnvironmentDetectorForTelemetry.IsCIEnvironment();

    public string? TenantId { get; protected set; } = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");

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
                        .ValidationErrorMessage("[red]Enter a valid tenant ID — a GUID or a domain like contoso.onmicrosoft.com[/]")
                        .Validate(IsValidTenantId),
                    cancellationToken);
                TenantId = tenantId;
            }
            else if (!IsValidTenantId(tenantId))
            {
                throw new InvalidOperationException(
                    $"Invalid AZURE_TENANT_ID value '{tenantId}'. " +
                    "It must be a tenant GUID or a domain such as contoso.onmicrosoft.com.");
            }

            logger.LogInformation("Signing in via Azure CLI...");
            var success = await RunAzLoginAsync(azPath, tenantId, cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException("Azure CLI login failed. Please try running 'az login' manually.");
            }

            // Retry with the now-valid Azure CLI credential
            var retryCredential = CreateAzureCliCredential();
            AccessToken retryToken;
            try
            {
                retryToken = await retryCredential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            }
            catch (AuthenticationFailedException ex)
            {
                throw new InvalidOperationException(
                    "Azure CLI login appeared to succeed but retrieving an access token failed. " +
                    "Try running 'az login' manually, then re-run the command.", ex);
            }

            logger.LogInformation("Authenticated via Azure CLI");
            return retryToken.Token;
        }
    }

    /// <summary>Creates the primary credential chain. Virtual to allow tests to substitute a fake.</summary>
    protected virtual TokenCredential CreateCredential()
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

    /// <summary>Creates the Azure CLI credential used after an interactive 'az login'. Virtual for tests.</summary>
    protected virtual TokenCredential CreateAzureCliCredential() => new AzureCliCredential();

    protected virtual string? FindAzureCli()
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

        // Fall back to PATH lookup. Launch where.exe by its absolute System32 path so a
        // malicious 'where.exe' in the current directory cannot be run instead (its own
        // resolution would otherwise search the working directory first).
        try
        {
            var whereExe = Path.Combine(Environment.SystemDirectory, "where.exe");
            if (!File.Exists(whereExe))
            {
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = whereExe,
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
                    // where.exe searches the current directory *before* PATH, so a malicious
                    // 'az.cmd' dropped into the working directory (e.g. an untrusted cloned repo)
                    // could appear first. Walk every candidate and return the first trusted one —
                    // a rooted path that does not live in the current working directory tree —
                    // instead of rejecting discovery outright when the first hit is untrusted.
                    var whereCandidates = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var candidate in whereCandidates)
                    {
                        if (IsTrustedAzureCliPath(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
        }
        catch
        {
            // where.exe not available or failed
        }

        return null;
    }

    /// <summary>
    /// Rejects an Azure CLI path resolved from <c>where.exe</c> when it is not a rooted,
    /// existing file or when it resolves anywhere inside the current working directory tree
    /// (the hijack vector — a repo could drop a malicious <c>az.cmd</c> in a subfolder such as
    /// <c>.\node_modules\.bin</c> that lands early on PATH).
    /// </summary>
    private static bool IsTrustedAzureCliPath(string resolvedPath) =>
        IsTrustedAzureCliPath(resolvedPath, Environment.CurrentDirectory);

    internal static bool IsTrustedAzureCliPath(string resolvedPath, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath)
            || !Path.IsPathRooted(resolvedPath)
            || !File.Exists(resolvedPath))
        {
            return false;
        }

        var resolvedDir = Path.GetFullPath(Path.GetDirectoryName(resolvedPath)!);
        var currentDir = Path.GetFullPath(currentDirectory);

        // Path.GetRelativePath yields "." when equal, a rooted path or a "..\" prefix when the
        // resolved directory is outside the cwd, and a plain relative segment when it is inside.
        var relative = Path.GetRelativePath(currentDir, resolvedDir);
        var isUnderCurrentDirectory = relative == "."
            || (!Path.IsPathRooted(relative)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && relative != "..");

        return !isUnderCurrentDirectory;
    }

    protected virtual async Task<bool> RunAzLoginAsync(string azPath, string tenantId, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = azPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false // Allow browser interaction
        };

        // Use ArgumentList rather than string interpolation so the (already validated)
        // tenant value can never be smuggled in as extra arguments to the az.cmd target.
        psi.ArgumentList.Add("login");
        psi.ArgumentList.Add("--tenant");
        psi.ArgumentList.Add(tenantId);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Azure CLI");

        // Drain and forward both pipes concurrently. Reading neither (the previous behavior)
        // can deadlock when az fills a redirected pipe, and forwarding lets the user see
        // device-login instructions written to stdout/stderr.
        var stdoutTask = ForwardStreamAsync(p.StandardOutput, cancellationToken);
        var stderrTask = ForwardStreamAsync(p.StandardError, cancellationToken);

        try
        {
            await p.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(p);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return p.ExitCode == 0;
    }

    private async Task ForwardStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            ansiConsole.WriteLine(line);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup on cancellation
        }
    }

    /// <summary>
    /// Validates that a tenant identifier is a GUID or a DNS-style domain name. This both
    /// prevents bad input from reaching the Azure CLI and rejects shell/argument metacharacters.
    /// </summary>
    internal static bool IsValidTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        return Guid.TryParse(tenantId, out _) || TenantDomainRegex().IsMatch(tenantId);
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$")]
    private static partial Regex TenantDomainRegex();

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

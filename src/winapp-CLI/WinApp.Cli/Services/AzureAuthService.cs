// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
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
internal partial class AzureAuthService(ILogger<AzureAuthService> logger, IAnsiConsole ansiConsole, IProcessRunner processRunner) : IAzureAuthService
{
    public virtual bool IsInteractive =>
        Environment.UserInteractive
        && !Console.IsInputRedirected
        && !CIEnvironmentDetectorForTelemetry.IsCIEnvironment();

    public string? TenantId { get; protected set; } = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");

    public async Task<string> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
var presetTenantId = TenantId;
        if (!string.IsNullOrEmpty(presetTenantId) && !IsValidTenantId(presetTenantId))
        {
            throw new InvalidOperationException(
                $"Invalid AZURE_TENANT_ID value '{presetTenantId}'. " +
                "It must be a tenant GUID or a domain such as contoso.onmicrosoft.com.");
        }

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
            // DefaultAzureCredential excludes AzureCliCredential (see CreateCredential), so a session
            // established by 'az login' — including the azure/login GitHub Actions flow in CI — is only
            // reachable through this explicit, validated-path lookup. Attempt it in EVERY environment
            // before giving up: resolve the CLI to a validated absolute path, then mint a token from any
            // existing session. Only launching a NEW interactive 'az login' (which prompts) is gated by
            // IsInteractive further below.
            var azPath = FindAzureCli();
            if (azPath != null)
            {
                var tenantId = TenantId;

                // A preset tenant must be well-formed before it is ever passed to the CLI (it rejects
                // both bad input and argument-injection metacharacters).
                if (!string.IsNullOrEmpty(tenantId) && !IsValidTenantId(tenantId))
                {
                    throw new InvalidOperationException(
                        $"Invalid AZURE_TENANT_ID value '{tenantId}'. " +
                        "It must be a tenant GUID or a domain such as contoso.onmicrosoft.com.");
                }

                // First try to mint a token from an existing Azure CLI session using the validated
                // absolute path. This preserves the zero-prompt experience for anyone who already ran
                // 'az login' — the case DefaultAzureCredential's (now excluded) CLI credential covered —
                // and, crucially, works in non-interactive CI where 'az login' ran earlier.
                var existingToken = await GetTokenViaAzureCliAsync(azPath, scope, tenantId, cancellationToken);
                if (existingToken != null)
                {
                    logger.LogInformation("Authenticated via Azure CLI");
                    return existingToken;
                }

                // No usable cached session. Launching a new interactive 'az login' only makes sense when
                // we can actually prompt; in non-interactive/CI environments fall through to guidance.
                if (IsInteractive)
                {
                    if (string.IsNullOrEmpty(tenantId))
                    {
                        tenantId = await ansiConsole.PromptAsync(
                            new TextPrompt<string>("Enter your [green]Azure Tenant ID[/] (found in Azure Portal > Azure AD > Overview):")
                                .ValidationErrorMessage("[red]Enter a valid tenant ID — a GUID or a domain like contoso.onmicrosoft.com[/]")
                                .Validate(IsValidTenantId),
                            cancellationToken);
                        TenantId = tenantId;
                    }

                    logger.LogInformation("Signing in via Azure CLI...");
                    var success = await RunAzLoginAsync(azPath, tenantId, cancellationToken);

                    if (!success)
                    {
                        throw new InvalidOperationException("Azure CLI login failed. Please try running 'az login' manually.");
                    }

                    // Retrieve the token from the now-valid session via the same validated absolute path.
                    var retryToken = await GetTokenViaAzureCliAsync(azPath, scope, tenantId, cancellationToken);
                    if (retryToken == null)
                    {
                        throw new InvalidOperationException(
                            "Azure CLI login appeared to succeed but retrieving an access token failed. " +
                            "Try running 'az login' manually, then re-run the command.");
                    }

                    logger.LogInformation("Authenticated via Azure CLI");
                    return retryToken;
                }
            }

            // Reached when no credentials worked: either the Azure CLI is not installed, or it is
            // installed but has no usable cached session and we cannot launch an interactive login.
            // Emit environment-appropriate guidance.
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

            throw new InvalidOperationException(
                "Azure authentication failed. To sign interactively, you have two options:\n\n" +
                "Option 1: Install the Azure CLI and run this command again (login will be handled automatically)\n" +
                "  Install from: https://aka.ms/installazurecli\n\n" +
                "Option 2: Set environment variables for a service principal:\n" +
                "  AZURE_TENANT_ID     - Your Azure AD tenant ID\n" +
                "  AZURE_CLIENT_ID     - Service principal application ID\n" +
                "  AZURE_CLIENT_SECRET - Service principal secret");
        }
    }

    /// <summary>Creates the primary credential chain. Virtual to allow tests to substitute a fake.</summary>
    protected virtual TokenCredential CreateCredential()
    {
        var options = new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,

            // Exclude the Azure CLI credential from the implicit chain: it invokes 'az' via an
            // ambient process lookup rather than the absolute path validated by FindAzureCli, so a
            // repo-local or PATH-hijacked az.cmd could run. CLI-based auth is instead handled
            // explicitly below through GetTokenViaAzureCliAsync using the validated path.
            ExcludeAzureCliCredential = true,
        };

        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        if (!string.IsNullOrEmpty(tenantId))
        {
            options.TenantId = tenantId;
        }

        return new DefaultAzureCredential(options);
    }

    /// <summary>
    /// Mints an access token from the current Azure CLI session by invoking <c>az account
    /// get-access-token</c> at the validated absolute <paramref name="azPath"/>. Returns null when
    /// no usable session exists or the token cannot be parsed, so callers can fall back to an
    /// interactive login. Virtual for tests.
    /// </summary>
    protected virtual async Task<string?> GetTokenViaAzureCliAsync(string azPath, string scope, string? tenantId, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "account",
            "get-access-token",
            "--scope",
            scope,
            "--output",
            "json",
        };

        // Only forward a tenant that has already been validated, so it can never carry extra args.
        if (!string.IsNullOrEmpty(tenantId) && IsValidTenantId(tenantId))
        {
            arguments.Add("--tenant");
            arguments.Add(tenantId);
        }

        ProcessRunResult result;
        try
        {
            result = await processRunner.RunAsync(
                new ProcessRunRequest(azPath, arguments, CreateNoWindow: true),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // Win32Exception: the az process could not be spawned (not found / not executable).
            // InvalidOperationException: ProcessRunner reports Process.Start returned null.
            // IOException: failure draining the process output streams.
            logger.LogDebug(ex, "Azure CLI token retrieval failed to start.");
            return null;
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            if (doc.RootElement.TryGetProperty("accessToken", out var tokenProp)
                && tokenProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var token = tokenProp.GetString();
                return string.IsNullOrEmpty(token) ? null : token;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Fall through — treat unparseable output as "no token".
        }

        return null;
    }

    protected virtual string? FindAzureCli()
    {
        // Check common install locations on Windows
        var candidates = new[]
        {
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
        };

        var installed = candidates.FirstOrDefault(File.Exists);
        if (installed != null)
        {
            return installed;
        }

        // Fall back to PATH lookup. Launch where.exe by its absolute System32 path so a
        // malicious 'where.exe' in the current directory cannot be run instead (its own
        // resolution would otherwise search the working directory first).
        try
        {
            var whereExe = Path.Join(Environment.SystemDirectory, "where.exe");
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
                    var trusted = SelectFirstTrustedAzureCliPath(output, GetAzureCliInstallRoots());
                    if (trusted != null)
                    {
                        return trusted;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // where.exe not available or failed to run: Win32Exception (cannot spawn),
            // InvalidOperationException (no process handle), or IOException (reading its output).
        }

        return null;
    }

    /// <summary>
    /// The known, machine-scoped Azure CLI installation roots on Windows. An <c>az.cmd</c>
    /// resolved from PATH is only trusted when it lives under one of these directories, so a
    /// repo- or user-writable copy elsewhere on PATH cannot be launched automatically.
    /// </summary>
    internal static IReadOnlyList<string> GetAzureCliInstallRoots()
    {
        var bases = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        return bases
            .Where(b => !string.IsNullOrEmpty(b))
            .Select(b => Path.Join(b, "Microsoft SDKs", "Azure", "CLI2"))
            .ToList();
    }

    /// <summary>
    /// Picks the first trusted Azure CLI path from raw <c>where.exe</c> output. where.exe searches
    /// the current directory *before* PATH, so a malicious 'az.cmd' dropped into the working
    /// directory (or anywhere else on PATH) could appear first. Walk every candidate and return the
    /// first one that lives under a known Azure CLI installation root, rather than trusting whatever
    /// happens to resolve first.
    /// </summary>
    internal static string? SelectFirstTrustedAzureCliPath(string whereOutput, IReadOnlyList<string> allowedRoots)
    {
        if (string.IsNullOrEmpty(whereOutput))
        {
            return null;
        }

        var whereCandidates = whereOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return whereCandidates.FirstOrDefault(candidate => IsTrustedAzureCliPath(candidate, allowedRoots));
    }

    /// <summary>
    /// Trusts an Azure CLI path resolved from <c>where.exe</c> only when it is a rooted, existing
    /// file located under one of the supplied known installation roots. "Outside the current
    /// directory" is deliberately not sufficient: a repo-controlled <c>az.cmd</c> in a sibling
    /// folder (e.g. <c>C:\repo\node_modules\.bin</c>) is outside the working subtree yet still
    /// attacker-controlled, so we require a positive match against a trusted install location.
    /// </summary>
    internal static bool IsTrustedAzureCliPath(string resolvedPath, IReadOnlyList<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath)
            || !Path.IsPathRooted(resolvedPath)
            || !File.Exists(resolvedPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(resolvedPath);

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var fullRoot = Path.GetFullPath(root);

            // Path.GetRelativePath yields "." when equal, a rooted path or a "..\" prefix when the
            // resolved path is outside the root, and a plain relative segment when it is inside.
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            var isUnderRoot = relative == "."
                || (!Path.IsPathRooted(relative)
                    && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));

            if (isUnderRoot)
            {
                return true;
            }
        }

        return false;
    }

    protected virtual async Task<bool> RunAzLoginAsync(string azPath, string tenantId, CancellationToken cancellationToken)
    {
        // Use an argument list rather than string interpolation so the (already validated) tenant
        // value can never be smuggled in as extra arguments to the az.cmd target. CreateNoWindow is
        // false so the CLI can pop a browser for interactive login. Both pipes are drained
        // concurrently and forwarded so the user sees device-login instructions; routing through
        // the logger lets --quiet suppress them.
        var result = await processRunner.RunAsync(
            new ProcessRunRequest(azPath, ["login", "--tenant", tenantId], CreateNoWindow: false),
            onOutputLine: line => logger.LogInformation("{AzCliOutput}", line),
            onErrorLine: line => logger.LogInformation("{AzCliOutput}", line),
            cancellationToken: cancellationToken);

        return result.ExitCode == 0;
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
            logger.LogInformation("Authenticated via cached credential");
        }
    }
}

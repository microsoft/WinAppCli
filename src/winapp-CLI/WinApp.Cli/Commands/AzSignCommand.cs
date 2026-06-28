// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Commands;

internal class AzSignCommand : Command, IShortDescription
{
    public string ShortDescription => "Code-sign a file using Azure Trusted Signing";

    public static Argument<FileInfo> FilePathArgument { get; }
    public static Option<string?> SubscriptionOption { get; }
    public static Option<string?> ResourceGroupOption { get; }
    public static Option<string?> AccountOption { get; }
    public static Option<string?> ProfileOption { get; }
    public static Option<FileInfo?> MetadataFileOption { get; }

    static AzSignCommand()
    {
        FilePathArgument = new Argument<FileInfo>("file-path")
        {
            Description = "Path to the file to sign (exe, msix, or msixbundle)"
        };
        FilePathArgument.AcceptExistingOnly();

        SubscriptionOption = new Option<string?>("--subscription", "-s")
        {
            Description = "Azure subscription ID to use. If not provided and multiple subscriptions exist, you will be prompted."
        };

        ResourceGroupOption = new Option<string?>("--resource-group", "-r")
        {
            Description = "Resource group to narrow down signing accounts"
        };

        AccountOption = new Option<string?>("--account", "-a")
        {
            Description = "Signing account name. Must be used with --resource-group"
        };

        ProfileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Certificate profile name. Must be used with --account"
        };

        MetadataFileOption = new Option<FileInfo?>("--metadata-file", "-m")
        {
            Description = "Path to an existing metadata.json file. Skips all prompting and uses this file directly for signing."
        };
    }

    public AzSignCommand() : base("az-sign", "Code-sign a file using Azure Trusted Signing. Signs executables, MSIX packages, or MSIX bundles using a cloud-managed signing identity. Example: winapp az-sign ./app.msix")
    {
        Arguments.Add(FilePathArgument);
        Options.Add(SubscriptionOption);
        Options.Add(ResourceGroupOption);
        Options.Add(AccountOption);
        Options.Add(ProfileOption);
        Options.Add(MetadataFileOption);
    }

    public class Handler(
        IAzureAuthService azureAuthService,
        IAzureSigningService azureSigningService,
        IBuildToolsService buildToolsService,
        INugetService nugetService,
        IPackageInstallationService packageInstallationService,
        IWinappDirectoryService winappDirectoryService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<Handler> logger) : AsynchronousCommandLineAction
    {
        internal const string TrustedSigningClientPackage = "Microsoft.ArtifactSigning.Client";
        private const string TimestampUrl = "http://timestamp.acs.microsoft.com";
        private const string ArmScope = "https://management.azure.com/.default";

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var filePath = parseResult.GetRequiredValue(FilePathArgument);
            var subscription = parseResult.GetValue(SubscriptionOption);
            var resourceGroup = parseResult.GetValue(ResourceGroupOption);
            var account = parseResult.GetValue(AccountOption);
            var profile = parseResult.GetValue(ProfileOption);
            var metadataFile = parseResult.GetValue(MetadataFileOption);

            // Validate flag dependencies
            if (account != null && resourceGroup == null)
            {
                logger.LogError("--account must be used with --resource-group");
                return 1;
            }

            if (profile != null && account == null)
            {
                logger.LogError("--profile must be used with --account");
                return 1;
            }

            try
            {
                // Step 1: Authenticate
                var accessToken = await azureAuthService.GetAccessTokenAsync(ArmScope, cancellationToken);

                if (string.IsNullOrEmpty(accessToken))
                {
                    logger.LogError("Failed to authenticate with Azure.");
                    return 1;
                }

                // Step 2-4: Determine what to sign with
                FileInfo metadataFilePath;
                bool generatedMetadata;

                if (metadataFile != null)
                {
                    if (!metadataFile.Exists)
                    {
                        logger.LogError("Metadata file not found: {Path}", metadataFile.FullName);
                        return 1;
                    }
                    metadataFilePath = metadataFile;
                    generatedMetadata = false;
                }
                else
                {
                    // Discover resources (may involve REST calls shown in status)
                    var metadata = await DiscoverAndSelectResourcesAsync(
                        accessToken, subscription, resourceGroup, account, profile, cancellationToken);

                    if (metadata == null)
                    {
                        return 1;
                    }

                    // Confirm selection (outside status context)
                    var confirm = await ansiConsole.PromptAsync(
                        new ConfirmationPrompt(
                            $"Sign with profile [green]{metadata.Value.ProfileName}[/] in account [green]{metadata.Value.AccountName}[/]?")
                        {
                            DefaultValue = true
                        },
                        cancellationToken);

                    if (!confirm)
                    {
                        return 1;
                    }

                    // Generate metadata.json
                    metadataFilePath = await GenerateMetadataFileAsync(metadata.Value, cancellationToken);
                    generatedMetadata = true;
                }

                // Step 5: Sign with signtool (inside status context)
                try
                {
                    return await statusService.ExecuteWithStatusAsync($"Signing: {filePath.Name}", async (taskContext, ct) =>
                    {
                        await SignWithSignToolAsync(filePath, metadataFilePath, taskContext, ct);
                        return (0, $"Successfully signed: {filePath.Name}");
                    }, cancellationToken);
                }
                finally
                {
                    if (generatedMetadata)
                    {
                        CleanupMetadataFile(metadataFilePath);
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError("{Message}", ex.Message);
                return 1;
            }
            catch (OperationCanceledException)
            {
                logger.LogError("Operation cancelled.");
                return 1;
            }
        }

        private async Task<SigningMetadata?> DiscoverAndSelectResourcesAsync(
            string accessToken, string? subscriptionId, string? resourceGroup,
            string? accountName, string? profileName,
            CancellationToken cancellationToken)
        {
            // Resolve subscription
            if (string.IsNullOrEmpty(subscriptionId))
            {
                subscriptionId = await SelectSubscriptionAsync(accessToken, cancellationToken);
                if (subscriptionId == null)
                {
                    return null;
                }
            }

            // Resolve signing account
            string resolvedResourceGroup;
            string resolvedAccountName;
            string? accountUri;

            if (!string.IsNullOrEmpty(accountName))
            {
                resolvedResourceGroup = resourceGroup!;
                resolvedAccountName = accountName;
                // We need the account URI — fetch it
                var accounts = await azureSigningService.ListSigningAccountsAsync(
                    accessToken, subscriptionId, resolvedResourceGroup, cancellationToken);
                var matchedAccount = accounts.FirstOrDefault(a =>
                    string.Equals(a.Name, accountName, StringComparison.OrdinalIgnoreCase));
                accountUri = matchedAccount?.AccountUri;

                if (matchedAccount == null)
                {
                    throw new InvalidOperationException(
                        $"Signing account '{accountName}' not found in resource group '{resolvedResourceGroup}'.");
                }
            }
            else
            {
                var selectedAccount = await SelectSigningAccountAsync(
                    accessToken, subscriptionId, resourceGroup, cancellationToken);
                if (selectedAccount == null)
                {
                    return null;
                }
                resolvedResourceGroup = selectedAccount.ResourceGroup;
                resolvedAccountName = selectedAccount.Name;
                accountUri = selectedAccount.AccountUri;
            }

            // Resolve certificate profile
            string resolvedProfileName;
            if (!string.IsNullOrEmpty(profileName))
            {
                resolvedProfileName = profileName;
            }
            else
            {
                var selectedProfile = await SelectCertificateProfileAsync(
                    accessToken, subscriptionId, resolvedResourceGroup, resolvedAccountName,
                    cancellationToken);
                if (selectedProfile == null)
                {
                    return null;
                }
                resolvedProfileName = selectedProfile;
            }

            // Determine endpoint from account URI or fall back to location-based
            var endpoint = accountUri ?? $"https://{resolvedAccountName}.codesigning.azure.net";

            return new SigningMetadata(endpoint, resolvedAccountName, resolvedProfileName);
        }

        private async Task<string?> SelectSubscriptionAsync(
            string accessToken, CancellationToken cancellationToken)
        {
            var subscriptions = await azureSigningService.ListSubscriptionsAsync(accessToken, cancellationToken);

            if (subscriptions.Count == 0)
            {
                throw new InvalidOperationException(
                    "No Azure subscriptions found. Ensure your account has access to at least one subscription.");
            }

            if (subscriptions.Count == 1)
            {
                return subscriptions[0].SubscriptionId;
            }

            // Prompt user to select
            var choices = subscriptions.Select(s => $"{s.DisplayName} ({s.SubscriptionId})").ToList();
            var prompt = new SelectionPrompt<string>()
                .Title("Select an Azure subscription:")
                .AddChoices(choices);

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);
            var index = choices.IndexOf(selected);
            return subscriptions[index].SubscriptionId;
        }

        private async Task<SigningAccount?> SelectSigningAccountAsync(
            string accessToken, string subscriptionId, string? resourceGroup,
            CancellationToken cancellationToken)
        {
            var accounts = await azureSigningService.ListSigningAccountsAsync(
                accessToken, subscriptionId, resourceGroup, cancellationToken);

            if (accounts.Count == 0)
            {
                var context = string.IsNullOrEmpty(resourceGroup)
                    ? "subscription"
                    : $"resource group '{resourceGroup}'";
                throw new InvalidOperationException(
                    $"No Trusted Signing accounts found in the {context}.\n" +
                    "Create one in the Azure portal or via 'az trustedsigning create'.");
            }

            if (accounts.Count == 1)
            {
                return accounts[0];
            }

            // Format choices based on whether resource group was provided
            List<string> choices;
            if (string.IsNullOrEmpty(resourceGroup))
            {
                choices = accounts.Select(a => $"{a.Name}, Resource Group: {a.ResourceGroup}").ToList();
            }
            else
            {
                choices = accounts.Select(a => a.Name).ToList();
            }

            var prompt = new SelectionPrompt<string>()
                .Title("Select a signing account:")
                .AddChoices(choices);

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);
            var index = choices.IndexOf(selected);
            return accounts[index];
        }

        private async Task<string?> SelectCertificateProfileAsync(
            string accessToken, string subscriptionId, string resourceGroup, string accountName,
            CancellationToken cancellationToken)
        {
            var profiles = await azureSigningService.ListCertificateProfilesAsync(
                accessToken, subscriptionId, resourceGroup, accountName, cancellationToken);

            if (profiles.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No certificate profiles found for signing account '{accountName}'.\n" +
                    "Create a certificate profile in the Azure portal after completing identity validation.");
            }

            if (profiles.Count == 1)
            {
                return profiles[0].Name;
            }

            var choices = profiles.Select(p => p.Name).ToList();
            var prompt = new SelectionPrompt<string>()
                .Title("Select a certificate profile:")
                .AddChoices(choices);

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);
            return selected;
        }

        private static async Task<FileInfo> GenerateMetadataFileAsync(SigningMetadata metadata, CancellationToken cancellationToken)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"winapp-az-sign-{Guid.NewGuid():N}.json");

            using var stream = File.Create(tempPath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("Endpoint", metadata.Endpoint);
            writer.WriteString("CodeSigningAccountName", metadata.AccountName);
            writer.WriteString("CertificateProfileName", metadata.ProfileName);
            // Exclude SharedTokenCacheCredential — it picks up stale consumer tokens from the MSAL
            // shared cache and fails because the Azure.CodeSigning app is AAD-only
            writer.WriteStartArray("ExcludeCredentials");
            writer.WriteStringValue("SharedTokenCacheCredential");
            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);

            return new FileInfo(tempPath);
        }

        private async Task SignWithSignToolAsync(FileInfo filePath, FileInfo metadataFilePath, TaskContext taskContext, CancellationToken cancellationToken)
        {
            // Ensure the Trusted Signing dlib is available
            var dlibPath = await EnsureTrustedSigningDlibAsync(taskContext, cancellationToken);

            // The dlib only ships as x64/x86. We must use the matching signtool architecture.
            // Determine dlib architecture from its path (bin/x64/ or bin/x86/)
            var dlibArch = dlibPath.Directory?.Name; // "x64" or "x86"

            // Ensure signtool is installed, then find the matching architecture version
            var signtoolPath = await buildToolsService.EnsureBuildToolAvailableAsync("signtool.exe", taskContext, cancellationToken: cancellationToken);

            // If we're on ARM64 and the signtool resolved is arm64, swap to x64 to match the dlib
            if (dlibArch != null && !signtoolPath.FullName.Contains($"\\{dlibArch}\\", StringComparison.OrdinalIgnoreCase))
            {
                // Try to find the x64 signtool alongside the resolved one
                var signtoolDir = signtoolPath.Directory!;
                var parentDir = signtoolDir.Parent;
                if (parentDir != null)
                {
                    var matchingArchSigntool = new FileInfo(Path.Combine(parentDir.FullName, dlibArch, "signtool.exe"));
                    if (matchingArchSigntool.Exists)
                    {
                        signtoolPath = matchingArchSigntool;
                    }
                }
            }

            // Build signtool arguments for Azure Trusted Signing
            var arguments = $@"sign /v /debug /fd SHA256 /tr ""{TimestampUrl}"" /td SHA256 /dlib ""{dlibPath.FullName}"" /dmdf ""{metadataFilePath.FullName}"" ""{filePath.FullName}""";

            taskContext.AddDebugMessage($"Using signtool: {signtoolPath.FullName}");
            taskContext.AddDebugMessage($"Using dlib: {dlibPath.FullName}");

            var psi = new ProcessStartInfo
            {
                FileName = signtoolPath.FullName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Pass tenant ID to signtool so the dlib's Azure.Identity authenticates against the correct tenant
            var tenantId = azureAuthService.TenantId;
            if (!string.IsNullOrEmpty(tenantId))
            {
                psi.Environment["AZURE_TENANT_ID"] = tenantId;
            }

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start signtool.exe process");
            var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                taskContext.AddDebugMessage(stdout);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                taskContext.AddDebugMessage(stderr);
            }

            if (p.ExitCode != 0)
            {
                var errorOutput = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                throw new InvalidOperationException($"signtool.exe execution failed with exit code {p.ExitCode}.\n{errorOutput}");
            }

            taskContext.AddDebugMessage("File signed successfully");
        }

        internal async Task<FileInfo> EnsureTrustedSigningDlibAsync(TaskContext taskContext, CancellationToken cancellationToken)
        {
            // Check if already available in NuGet cache
            var dlibPath = FindTrustedSigningDlib();
            if (dlibPath != null)
            {
                return dlibPath;
            }

            // Download the package
            await taskContext.AddSubTaskAsync($"Installing {TrustedSigningClientPackage}...", async (subContext, ct) =>
            {
                var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
                var success = await packageInstallationService.EnsurePackageAsync(
                    globalWinappDir,
                    TrustedSigningClientPackage,
                    subContext,
                    cancellationToken: ct);

                if (!success)
                {
                    return (1, $"Failed to install {TrustedSigningClientPackage}.");
                }

                return (0, $"{TrustedSigningClientPackage} installed successfully.");
            }, cancellationToken);

            dlibPath = FindTrustedSigningDlib();
            if (dlibPath == null)
            {
                throw new InvalidOperationException(
                    $"Could not find the Trusted Signing client library after installing {TrustedSigningClientPackage}.\n" +
                    "Ensure the package contains the expected DLL structure.");
            }

            return dlibPath;
        }

        private FileInfo? FindTrustedSigningDlib()
        {
            var nugetCache = nugetService.GetNuGetGlobalPackagesDir();
            var packageDir = new DirectoryInfo(Path.Combine(nugetCache.FullName, TrustedSigningClientPackage.ToLowerInvariant()));

            if (!packageDir.Exists)
            {
                return null;
            }

            // Find the latest version directory
            var versionDirs = packageDir.GetDirectories()
                .OrderByDescending(d => d.Name)
                .ToArray();

            foreach (var versionDir in versionDirs)
            {
                // The dlib is at: bin/x64/Azure.CodeSigning.Dlib.dll (x64 works on ARM64 via emulation)
                var dlibFile = new FileInfo(Path.Combine(versionDir.FullName, "bin", "x64", "Azure.CodeSigning.Dlib.dll"));
                if (dlibFile.Exists)
                {
                    return dlibFile;
                }

                // Fallback: search recursively for the DLL
                var found = versionDir.GetFiles("Azure.CodeSigning.Dlib.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void CleanupMetadataFile(FileInfo metadataFile)
        {
            try
            {
                metadataFile.Refresh();
                if (metadataFile.Exists)
                {
                    metadataFile.Delete();
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }

        private readonly record struct SigningMetadata(string Endpoint, string AccountName, string ProfileName);
    }
}

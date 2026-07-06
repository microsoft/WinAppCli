// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Services;

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

        AccountOption = new Option<string?>("--account")
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
        IAzureSignToolService azureSignToolService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<Handler> logger) : AsynchronousCommandLineAction
    {
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

            // Validate the metadata file exists before doing any Azure work, so an obvious
            // input error fails fast rather than after an interactive login.
            if (metadataFile != null && !metadataFile.Exists)
            {
                logger.LogError("Metadata file not found: {Path}", metadataFile.FullName);
                return 1;
            }

            try
            {
                // Determine what to sign with
                FileInfo metadataFilePath;
                bool generatedMetadata;

                if (metadataFile != null)
                {
                    metadataFilePath = metadataFile;
                    generatedMetadata = false;
                }
                else
                {
                    // Authenticate for ARM resource discovery (not needed when metadata file is provided,
                    // since the dlib authenticates independently for the signing data plane)
                    var accessToken = await azureAuthService.GetAccessTokenAsync(ArmScope, cancellationToken);

                    if (string.IsNullOrEmpty(accessToken))
                    {
                        logger.LogError("Failed to authenticate with Azure.");
                        return 1;
                    }

                    // Discover resources (may involve REST calls shown in status)
                    var metadata = await DiscoverAndSelectResourcesAsync(
                        accessToken, subscription, resourceGroup, account, profile, cancellationToken);

                    if (metadata == null)
                    {
                        return 1;
                    }

                    // Confirm selection (outside status context) unless the signing identity
                    // was fully specified on the command line, or we're running non-interactively
                    // (where a prompt would only hang/fail automated callers).
                    var fullySpecified = account != null && profile != null;
                    if (!fullySpecified && azureAuthService.IsInteractive)
                    {
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
                        await azureSignToolService.SignAsync(filePath, metadataFilePath, azureAuthService.TenantId, taskContext, ct);
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

            // The endpoint must come from the account's accountUri property (regional, e.g. https://eus.codesigning.azure.net)
            if (string.IsNullOrEmpty(accountUri))
            {
                throw new InvalidOperationException(
                    $"Signing account '{resolvedAccountName}' does not have an endpoint URI. " +
                    "The account may still be provisioning. Please try again in a few minutes, " +
                    "or specify a metadata file with --metadata-file.");
            }

            return new SigningMetadata(accountUri, resolvedAccountName, resolvedProfileName);
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

            var choices = profiles.Select(p => $"{p.Name} ({p.ProfileType})").ToList();
            var prompt = new SelectionPrompt<string>()
                .Title("Select a certificate profile:")
                .AddChoices(choices);

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);
            // Strip the profile type suffix to get just the name
            return selected[..selected.LastIndexOf(" (")];
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

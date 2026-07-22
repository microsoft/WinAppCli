// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzSignCommandTests : BaseCommandTests
{
    private FakeAzureAuthService _fakeAuthService = null!;
    private FakeAzureSigningService _fakeSigningService = null!;
    private FakeAzureSignToolService _fakeSignToolService = null!;
    private AzSignCommand _command = null!;

    [TestInitialize]
    public void Setup()
    {
        _fakeAuthService = (FakeAzureAuthService)GetRequiredService<IAzureAuthService>();
        _fakeSigningService = (FakeAzureSigningService)GetRequiredService<IAzureSigningService>();
        _fakeSignToolService = (FakeAzureSignToolService)GetRequiredService<IAzureSignToolService>();
        _command = GetRequiredService<AzSignCommand>();
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IAzureAuthService, FakeAzureAuthService>()
            .AddSingleton<IAzureSigningService, FakeAzureSigningService>()
            .AddSingleton<IAzureSignToolService, FakeAzureSignToolService>();
    }

    [TestMethod]
    public async Task AzSign_AccountWithoutResourceGroup_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--account", "myaccount", filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "--account must be used with --resource-group");
    }

    [TestMethod]
    public async Task AzSign_ProfileWithoutAccount_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--profile", "myprofile", filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "--profile must be used with --account");
    }

    [TestMethod]
    public async Task AzSign_AuthenticationFails_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeAuthService.ShouldFail = true;
        _fakeAuthService.FailureMessage = "Azure authentication failed. No credentials found.";

        var result = await ParseAndInvokeWithCaptureAsync(_command, [filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "Azure authentication failed");
    }

    [TestMethod]
    public async Task AzSign_NoSubscriptionsFound_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions = [];

        var result = await ParseAndInvokeWithCaptureAsync(_command, [filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "No Azure subscriptions found");
    }

    [TestMethod]
    public async Task AzSign_NoSigningAccountsFound_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions =
        [
            new AzureSubscription("sub-123", "Test Subscription")
        ];
        _fakeSigningService.SigningAccounts = [];

        var result = await ParseAndInvokeWithCaptureAsync(_command, [filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "No Trusted Signing accounts found");
    }

    [TestMethod]
    public async Task AzSign_NoProfilesFound_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions =
        [
            new AzureSubscription("sub-123", "Test Subscription")
        ];
        _fakeSigningService.SigningAccounts =
        [
            new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")
        ];
        _fakeSigningService.CertificateProfiles = [];

        var result = await ParseAndInvokeWithCaptureAsync(_command, [filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "No certificate profiles found");
    }

    [TestMethod]
    public async Task AzSign_MetadataFileNotFound_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        var nonExistentMetadata = Path.Combine(_tempDirectory.FullName, "nonexistent.json");

        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--metadata-file", nonExistentMetadata, filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "Metadata file not found");
    }

    [TestMethod]
    public async Task AzSign_SpecifiedAccountNotFound_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions =
        [
            new AzureSubscription("sub-123", "Test Subscription")
        ];
        _fakeSigningService.SigningAccounts = [];

        var result = await ParseAndInvokeWithCaptureAsync(_command,
            ["--subscription", "sub-123", "--resource-group", "myrg", "--account", "nonexistent", filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "not found");
    }

    [TestMethod]
    public async Task AzSign_SingleSubscription_AutoSelects()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions =
        [
            new AzureSubscription("sub-123", "Test Subscription")
        ];
        _fakeSigningService.SigningAccounts =
        [
            new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")
        ];
        _fakeSigningService.CertificateProfiles =
        [
            new CertificateProfile("myprofile", "PublicTrust", "Active")
        ];

        // Accept the confirmation prompt (single sub/account/profile are auto-selected first).
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var result = await ParseAndInvokeWithCaptureAsync(_command, [filePath]);

        // Reaches the signing stage and succeeds via the faked sign-tool service.
        Assert.AreEqual(0, result);
        Assert.AreEqual(1, _fakeSignToolService.CallCount, "Should reach the signing stage");
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        Assert.IsFalse(allOutput.Contains("No Azure subscriptions found"), "Should not fail at subscription selection");
        Assert.IsFalse(allOutput.Contains("No Trusted Signing accounts found"), "Should not fail at account selection");
        Assert.IsFalse(allOutput.Contains("No certificate profiles found"), "Should not fail at profile selection");
    }

    [TestMethod]
    public async Task AzSign_MultipleSubscriptions_WithFlag_SkipsPrompt()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions =
        [
            new AzureSubscription("sub-123", "Sub 1"),
            new AzureSubscription("sub-456", "Sub 2")
        ];
        _fakeSigningService.SigningAccounts =
        [
            new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")
        ];
        _fakeSigningService.CertificateProfiles =
        [
            new CertificateProfile("myprofile", "PublicTrust", "Active")
        ];

        // Account/profile are auto-selected (single), so the sign confirmation still prompts.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        // Using --subscription flag should skip the subscription prompt
        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--subscription", "sub-123", filePath]);

        Assert.AreEqual(0, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        Assert.IsFalse(allOutput.Contains("Select an Azure subscription"), "Should not prompt for subscription when flag is provided");
    }

    [TestMethod]
    public async Task AzSign_AllFlagsProvided_SkipsAllPrompting()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions =
        [
            new AzureSubscription("sub-123", "Test Subscription")
        ];
        _fakeSigningService.SigningAccounts =
        [
            new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")
        ];
        _fakeSigningService.CertificateProfiles =
        [
            new CertificateProfile("myprofile", "PublicTrust", "Active")
        ];

        // No prompt input pushed: with account+profile supplied, no confirmation should be required.
        var result = await ParseAndInvokeWithCaptureAsync(_command,
            ["--subscription", "sub-123", "--resource-group", "myrg", "--account", "myaccount", "--profile", "myprofile", filePath]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, _fakeSignToolService.CallCount, "Should reach the signing stage without prompting");
        Assert.AreEqual("fake-tenant-id", _fakeSignToolService.LastTenantId, "Tenant ID should be forwarded to signtool");
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        Assert.IsFalse(allOutput.Contains("Select"), "Should not show any selection prompts when all flags are provided");
        Assert.IsFalse(allOutput.Contains("Sign with profile"), "Should not show a confirmation prompt when account and profile are explicit");
    }

    [TestMethod]
    public async Task AzSign_AllFlagsProvided_GeneratesMetadataAndCleansUp()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions = [new AzureSubscription("sub-123", "Test Subscription")];
        _fakeSigningService.SigningAccounts = [new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")];
        _fakeSigningService.CertificateProfiles = [new CertificateProfile("myprofile", "PublicTrust", "Active")];

        var result = await ParseAndInvokeWithCaptureAsync(_command,
            ["--subscription", "sub-123", "--resource-group", "myrg", "--account", "myaccount", "--profile", "myprofile", filePath]);

        Assert.AreEqual(0, result);

        // The generated metadata file should contain the resolved signing identity...
        Assert.IsNotNull(_fakeSignToolService.LastMetadataContent);
        StringAssert.Contains(_fakeSignToolService.LastMetadataContent, "myaccount");
        StringAssert.Contains(_fakeSignToolService.LastMetadataContent, "myprofile");
        StringAssert.Contains(_fakeSignToolService.LastMetadataContent, "https://eus.codesigning.azure.net");

        // ...and the temp file must be cleaned up afterward.
        Assert.IsNotNull(_fakeSignToolService.LastMetadataFilePath);
        Assert.IsFalse(File.Exists(_fakeSignToolService.LastMetadataFilePath!.FullName), "Generated metadata file should be deleted after signing");
    }

    [TestMethod]
    public async Task AzSign_WithMetadataFile_UsesFileDirectly_DoesNotDelete()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        var metadataPath = Path.Combine(_tempDirectory.FullName, "metadata.json");
        await File.WriteAllTextAsync(metadataPath, "{\"Endpoint\":\"https://eus.codesigning.azure.net\"}");

        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--metadata-file", metadataPath, filePath]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, _fakeSignToolService.CallCount);
        Assert.AreEqual(metadataPath, _fakeSignToolService.LastMetadataFilePath!.FullName);
        // A user-supplied metadata file must not be deleted.
        Assert.IsTrue(File.Exists(metadataPath), "User-provided metadata file should not be deleted");
    }

    [TestMethod]
    public async Task AzSign_SignToolFails_ReturnsError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions = [new AzureSubscription("sub-123", "Test Subscription")];
        _fakeSigningService.SigningAccounts = [new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")];
        _fakeSigningService.CertificateProfiles = [new CertificateProfile("myprofile", "PublicTrust", "Active")];
        _fakeSignToolService.ShouldFail = true;

        var result = await ParseAndInvokeWithCaptureAsync(_command,
            ["--subscription", "sub-123", "--resource-group", "myrg", "--account", "myaccount", "--profile", "myprofile", filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "signtool.exe execution failed");
    }

    [TestMethod]
    public async Task AzSign_NonInteractive_MultipleSubscriptions_ReturnsActionableError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeAuthService.IsInteractive = false;
        _fakeSigningService.Subscriptions =
        [
            new AzureSubscription("sub-123", "Sub 1"),
            new AzureSubscription("sub-456", "Sub 2")
        ];

        var result = await ParseAndInvokeWithCaptureAsync(_command, [filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "--subscription");
    }

    [TestMethod]
    public async Task AzSign_NonInteractive_MultipleAccounts_ReturnsActionableError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeAuthService.IsInteractive = false;
        _fakeSigningService.Subscriptions = [new AzureSubscription("sub-123", "Test Subscription")];
        _fakeSigningService.SigningAccounts =
        [
            new SigningAccount("account-a", "rg-a", "eastus", "https://eus.codesigning.azure.net"),
            new SigningAccount("account-b", "rg-b", "westus", "https://wus.codesigning.azure.net")
        ];

        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--subscription", "sub-123", filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "--account");
    }

    [TestMethod]
    public async Task AzSign_NonInteractive_MultipleProfiles_ReturnsActionableError()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeAuthService.IsInteractive = false;
        _fakeSigningService.Subscriptions = [new AzureSubscription("sub-123", "Test Subscription")];
        _fakeSigningService.SigningAccounts =
        [
            new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")
        ];
        _fakeSigningService.CertificateProfiles =
        [
            new CertificateProfile("profile-a", "PublicTrust", "Active"),
            new CertificateProfile("profile-b", "PrivateTrust", "Active")
        ];

        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--subscription", "sub-123", filePath]);

        Assert.AreEqual(1, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        StringAssert.Contains(allOutput, "--profile");
    }

    [TestMethod]
    public async Task AzSign_WhitespaceOnlyProfile_IsTreatedAsNotProvided()
    {
        var filePath = Path.Combine(_tempDirectory.FullName, "test.exe");
        await File.WriteAllTextAsync(filePath, "MZ");

        _fakeSigningService.Subscriptions = [new AzureSubscription("sub-123", "Test Subscription")];
        _fakeSigningService.SigningAccounts = [new SigningAccount("myaccount", "myrg", "eastus", "https://eus.codesigning.azure.net")];
        _fakeSigningService.CertificateProfiles = [new CertificateProfile("myprofile", "PublicTrust", "Active")];

        // A whitespace-only --profile must normalize to "not provided" rather than tripping the
        // "--profile must be used with --account" dependency check; the single profile auto-selects.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--profile", "   ", filePath]);

        Assert.AreEqual(0, result);
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        Assert.IsFalse(allOutput.Contains("--profile must be used with --account"),
            "Whitespace-only --profile should be treated as not provided");
        Assert.AreEqual(1, _fakeSignToolService.CallCount, "Should reach the signing stage");
    }
}

internal class FakeAzureAuthService : IAzureAuthService
{
    public bool ShouldFail { get; set; }
    public string FailureMessage { get; set; } = "Authentication failed";
    public bool IsInteractive { get; set; } = true;
    public string? TenantId { get; set; } = "fake-tenant-id";

    public Task<string> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (ShouldFail)
        {
            throw new InvalidOperationException(FailureMessage);
        }
        return Task.FromResult("fake-access-token");
    }
}

internal class FakeAzureSignToolService : IAzureSignToolService
{
    public bool ShouldFail { get; set; }
    public int CallCount { get; private set; }
    public FileInfo? LastFilePath { get; private set; }
    public FileInfo? LastMetadataFilePath { get; private set; }
    public string? LastMetadataContent { get; private set; }
    public string? LastTenantId { get; private set; }

    public async Task SignAsync(
        FileInfo filePath,
        FileInfo metadataFilePath,
        string? tenantId,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastFilePath = filePath;
        LastMetadataFilePath = metadataFilePath;
        LastTenantId = tenantId;

        // Capture the metadata contents now, since the handler deletes generated files after we return.
        metadataFilePath.Refresh();
        if (metadataFilePath.Exists)
        {
            LastMetadataContent = await File.ReadAllTextAsync(metadataFilePath.FullName, cancellationToken);
        }

        if (ShouldFail)
        {
            throw new InvalidOperationException("signtool.exe execution failed with exit code 1.");
        }
    }
}

internal class FakeAzureSigningService : IAzureSigningService
{
    public IReadOnlyList<AzureSubscription> Subscriptions { get; set; } =
    [
        new AzureSubscription("sub-123", "Test Subscription")
    ];

    public IReadOnlyList<SigningAccount> SigningAccounts { get; set; } =
    [
        new SigningAccount("test-account", "test-rg", "eastus", "https://eus.codesigning.azure.net")
    ];

    public IReadOnlyList<CertificateProfile> CertificateProfiles { get; set; } =
    [
        new CertificateProfile("test-profile", "PublicTrust", "Active")
    ];

    public Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Subscriptions);
    }

    public Task<IReadOnlyList<SigningAccount>> ListSigningAccountsAsync(string accessToken, string subscriptionId, string? resourceGroup = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SigningAccounts);
    }

    public Task<IReadOnlyList<CertificateProfile>> ListCertificateProfilesAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CertificateProfiles);
    }
}

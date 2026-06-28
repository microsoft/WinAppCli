// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzSignCommandTests : BaseCommandTests
{
    private FakeAzureAuthService _fakeAuthService = null!;
    private FakeAzureSigningService _fakeSigningService = null!;
    private AzSignCommand _command = null!;

    [TestInitialize]
    public void Setup()
    {
        _fakeAuthService = (FakeAzureAuthService)GetRequiredService<IAzureAuthService>();
        _fakeSigningService = (FakeAzureSigningService)GetRequiredService<IAzureSigningService>();
        _command = GetRequiredService<AzSignCommand>();
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IAzureAuthService, FakeAzureAuthService>()
            .AddSingleton<IAzureSigningService, FakeAzureSigningService>();
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

        // Will proceed through auto-selection but fail at dlib/signtool stage (expected in test env)
        var result = await ParseAndInvokeWithCaptureAsync(_command, [filePath]);

        // It should fail at the signing stage (not at selection), so result is still 1
        // but the error should be about the dlib/signing, not about subscription/account/profile selection
        Assert.AreEqual(1, result);
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

        // Using --subscription flag should skip the subscription prompt
        var result = await ParseAndInvokeWithCaptureAsync(_command, ["--subscription", "sub-123", filePath]);

        // Should get past selection without prompting (will fail at signing stage)
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

        var result = await ParseAndInvokeWithCaptureAsync(_command,
            ["--subscription", "sub-123", "--resource-group", "myrg", "--account", "myaccount", "--profile", "myprofile", filePath]);

        // Should skip all prompting (will fail at signing since no signtool in test)
        var allOutput = ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        Assert.IsFalse(allOutput.Contains("Select"), "Should not show any selection prompts when all flags are provided");
    }
}

internal class FakeAzureAuthService : IAzureAuthService
{
    public bool ShouldFail { get; set; }
    public string FailureMessage { get; set; } = "Authentication failed";
    public bool IsInteractive => true;
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

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzureAuthServiceTests
{
    [TestMethod]
    [DataRow("72f988bf-86f1-41af-91ab-2d7cd011db47")] // GUID tenant
    [DataRow("contoso.onmicrosoft.com")]              // domain tenant
    [DataRow("my-tenant.example.co.uk")]
    public void IsValidTenantId_AcceptsGuidsAndDomains(string tenantId)
    {
        Assert.IsTrue(AzureAuthService.IsValidTenantId(tenantId));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not a tenant")]                       // whitespace
    [DataRow("tenant && rm -rf /")]                 // shell metacharacters
    [DataRow("--query \"[].id\"")]                  // extra CLI argument injection
    [DataRow("tenant\"; calc.exe; \"")]
    [DataRow("tenant`whoami`")]
    [DataRow("contoso.onmicrosoft.com --debug")]
    public void IsValidTenantId_RejectsInvalidOrUnsafeInput(string tenantId)
    {
        Assert.IsFalse(AzureAuthService.IsValidTenantId(tenantId));
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_RejectsFileInCurrentDirectory()
    {
        var baseDir = Directory.CreateTempSubdirectory("winapp-aztrust-cwd").FullName;
        try
        {
            var azPath = Path.Combine(baseDir, "az.cmd");
            File.WriteAllText(azPath, "@echo off");

            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath(azPath, baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_RejectsFileInSubdirectoryOfCurrentDirectory()
    {
        var baseDir = Directory.CreateTempSubdirectory("winapp-aztrust-sub").FullName;
        try
        {
            var subDir = Path.Combine(baseDir, "node_modules", ".bin");
            Directory.CreateDirectory(subDir);
            var azPath = Path.Combine(subDir, "az.cmd");
            File.WriteAllText(azPath, "@echo off");

            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath(azPath, baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_AcceptsFileOutsideCurrentDirectoryTree()
    {
        var baseDir = Directory.CreateTempSubdirectory("winapp-aztrust-base").FullName;
        var installDir = Directory.CreateTempSubdirectory("winapp-aztrust-install").FullName;
        try
        {
            var azPath = Path.Combine(installDir, "az.cmd");
            File.WriteAllText(azPath, "@echo off");

            Assert.IsTrue(AzureAuthService.IsTrustedAzureCliPath(azPath, baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
            Directory.Delete(installDir, recursive: true);
        }
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_RejectsNonExistentOrRelativePath()
    {
        var baseDir = Directory.CreateTempSubdirectory("winapp-aztrust-none").FullName;
        try
        {
            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath(Path.Combine(baseDir, "missing.cmd"), baseDir));
            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath("az.cmd", baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private const string ArmScope = "https://management.azure.com/.default";

    [TestMethod]
    public async Task GetAccessTokenAsync_WhenPrimaryCredentialSucceeds_ReturnsToken()
    {
        var service = new TestableAzureAuthService
        {
            PrimaryCredential = new StubCredential("primary-token"),
        };

        var token = await service.GetAccessTokenAsync(ArmScope);

        Assert.AreEqual("primary-token", token);
        Assert.AreEqual(0, service.RunAzLoginCallCount, "Should not fall back to az login when the primary credential works");
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_NonInteractiveAndCredentialFails_ThrowsCiGuidance()
    {
        var service = new TestableAzureAuthService
        {
            InteractiveOverride = false,
            PrimaryCredential = new ThrowingCredential(),
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        StringAssert.Contains(ex.Message, "AZURE_CLIENT_SECRET");
        Assert.AreEqual(0, service.RunAzLoginCallCount, "Non-interactive sessions must never launch az login");
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InteractiveButNoAzureCli_ThrowsInstallGuidance()
    {
        var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = null,
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        StringAssert.Contains(ex.Message, "Install the Azure CLI");
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InteractiveLoginSucceeds_RetriesWithCliCredential()
    {
        var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            LoginResult = true,
            CliCredential = new StubCredential("cli-token"),
            SeedTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        };

        var token = await service.GetAccessTokenAsync(ArmScope);

        Assert.AreEqual("cli-token", token);
        Assert.AreEqual(1, service.RunAzLoginCallCount);
        Assert.AreEqual("72f988bf-86f1-41af-91ab-2d7cd011db47", service.LastLoginTenantId);
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InteractiveLoginFails_Throws()
    {
        var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            LoginResult = false,
            SeedTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        StringAssert.Contains(ex.Message, "Azure CLI login failed");
    }

    private sealed class TestableAzureAuthService : AzureAuthService
    {
        public TestableAzureAuthService()
            : base(NullLogger<AzureAuthService>.Instance, new TestConsole())
        {
        }

        public bool? InteractiveOverride { get; init; }
        public TokenCredential PrimaryCredential { get; init; } = new ThrowingCredential();
        public TokenCredential CliCredential { get; init; } = new StubCredential("cli-token");
        public string? AzCliPath { get; init; }
        public bool LoginResult { get; init; }
        public int RunAzLoginCallCount { get; private set; }
        public string? LastLoginTenantId { get; private set; }

        // Pre-seed the tenant so the interactive paths don't block on a prompt.
        public string? SeedTenantId { init => TenantId = value; }

        public override bool IsInteractive => InteractiveOverride ?? base.IsInteractive;

        protected override TokenCredential CreateCredential() => PrimaryCredential;

        protected override TokenCredential CreateAzureCliCredential() => CliCredential;

        protected override string? FindAzureCli() => AzCliPath;

        protected override Task<bool> RunAzLoginAsync(string azPath, string tenantId, CancellationToken cancellationToken)
        {
            RunAzLoginCallCount++;
            LastLoginTenantId = tenantId;
            return Task.FromResult(LoginResult);
        }
    }

    private sealed class StubCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class ThrowingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new AuthenticationFailedException("no credentials");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new AuthenticationFailedException("no credentials");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class AuthenticodeVerifierTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Authenticode_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void IsMicrosoftSubject_MicrosoftOrganization_ReturnsTrue()
    {
        Assert.IsTrue(AuthenticodeVerifier.IsMicrosoftSubject(
            "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_MicrosoftCommonName_ReturnsTrue()
    {
        Assert.IsTrue(AuthenticodeVerifier.IsMicrosoftSubject("CN=Microsoft Corporation"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_CaseInsensitive_ReturnsTrue()
    {
        Assert.IsTrue(AuthenticodeVerifier.IsMicrosoftSubject("cn=microsoft corporation, o=microsoft corporation"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_ThirdParty_ReturnsFalse()
    {
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject(
            "CN=Contoso Ltd, O=Contoso Corporation, C=US"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_LookalikeWithoutMicrosoftMarkers_ReturnsFalse()
    {
        // "Microsoftish" text that is not an O=Microsoft Corporation or CN=Microsoft* subject.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("O=Not Microsoft-Affiliated Vendor, CN=Acme"));
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_NonexistentFile_ReturnsFalse()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.dll");

        Assert.IsFalse(AuthenticodeVerifier.IsTrustedMicrosoftSigned(missing, NullLogger.Instance),
            "A missing file must fail the fail-closed trust gate.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_UnsignedFile_ReturnsFalse()
    {
        var unsigned = Path.Combine(_tempDir, "unsigned.dll");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04]);

        Assert.IsFalse(AuthenticodeVerifier.IsTrustedMicrosoftSigned(unsigned, NullLogger.Instance),
            "An unsigned file must not pass the Authenticode trust gate.");
    }
}

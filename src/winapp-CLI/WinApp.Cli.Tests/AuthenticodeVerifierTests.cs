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

    // HRESULTs mirrored from the production constants (which are private).
    private const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    private const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
    private const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);

    [TestMethod]
    public void IsTrustedMicrosoftSigned_TrustOkAndMicrosoftSigner_ReturnsTrue()
    {
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => 0,
            isMicrosoftSigner: _ => true);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_TrustOkButNonMicrosoftSigner_ReturnsFalse()
    {
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => 0,
            isMicrosoftSigner: _ => false);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_CertificateRevoked_ReturnsFalse()
    {
        var signerCalled = false;
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => CERT_E_REVOKED,
            isMicrosoftSigner: _ => { signerCalled = true; return true; });

        Assert.IsFalse(result, "A revoked certificate is a hard failure.");
        Assert.IsFalse(signerCalled, "Signer check must be skipped once trust verification fails.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_RevocationOffline_FallsBackToSignatureOnly_AndPasses()
    {
        // First call (whole-chain) reports revocation data unavailable; the fallback signature-only
        // call succeeds, so trust verification passes and the Microsoft signer gate then applies.
        var call = 0;
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => ++call == 1 ? CERT_E_REVOCATION_FAILURE : 0,
            isMicrosoftSigner: _ => true);

        Assert.IsTrue(result);
        Assert.AreEqual(2, call, "The fallback signature-only verification must run.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_RevocationOffline_FallbackAlsoFails_ReturnsFalse()
    {
        var call = 0;
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => ++call == 1 ? CRYPT_E_REVOCATION_OFFLINE : unchecked((int)0x80070005),
            isMicrosoftSigner: _ => true);

        Assert.IsFalse(result);
        Assert.AreEqual(2, call);
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_UnrecognizedTrustError_ReturnsFalse()
    {
        // Any other WinVerifyTrust HRESULT (e.g. TRUST_E_NOSIGNATURE) is an untrusted result.
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => unchecked((int)0x800B0100),
            isMicrosoftSigner: _ => true);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_VerifierThrows_IsCaught_ReturnsFalse()
    {
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => throw new InvalidOperationException("boom"),
            isMicrosoftSigner: _ => true);

        Assert.IsFalse(result, "An exception during verification must be caught and fail closed.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_RealMicrosoftSignedBinary_ReturnsTrue()
    {
        // Exercises the real native trust + signer extraction path against known embedded-signed
        // Microsoft OS binaries. At least one of these must validate on a healthy Windows install.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] candidates =
        [
            Path.Combine(system32, "dllhost.exe"),
            Path.Combine(system32, "taskhostw.exe"),
            Path.Combine(windows, "explorer.exe"),
        ];

        var anyTrusted = candidates
            .Where(File.Exists)
            .Any(f => AuthenticodeVerifier.IsTrustedMicrosoftSigned(f, NullLogger.Instance));

        Assert.IsTrue(anyTrusted,
            "A trusted, embedded-signed Microsoft binary must pass the Authenticode gate.");
    }
}

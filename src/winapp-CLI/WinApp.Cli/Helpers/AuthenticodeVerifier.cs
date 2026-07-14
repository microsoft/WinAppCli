// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Verifies that a file on disk carries a valid Authenticode signature that chains to a trusted root
/// and is signed by Microsoft. Used as a defense-in-depth integrity check on native binaries that are
/// downloaded and then loaded into a debugger process (e.g. <c>JsProvider.dll</c> from the WinDbg
/// bundle), so a tampered or substituted file is rejected even though it is fetched over HTTPS from an
/// official Microsoft host.
/// </summary>
internal static unsafe partial class AuthenticodeVerifier
{
    // WINTRUST_ACTION_GENERIC_VERIFY_V2 — standard Authenticode policy provider.
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    // HRESULTs distinguishing "certificate is revoked" (hard fail) from "revocation data unavailable"
    // (soft fail — acceptable offline, where only the signature itself can be checked).
    private const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    private const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
    private const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);
    private const int CRYPT_E_NO_REVOCATION_CHECK = unchecked((int)0x80092012);

    /// <summary>
    /// Returns <c>true</c> only when <paramref name="filePath"/> has a valid Authenticode signature
    /// that chains to a trusted root <em>and</em> whose signer is Microsoft. Any failure (unsigned,
    /// untrusted, non-Microsoft, or verification error) returns <c>false</c> — this is a fail-closed
    /// security gate.
    /// </summary>
    public static bool IsTrustedMicrosoftSigned(string filePath, ILogger logger)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            if (!VerifyTrust(filePath, logger))
            {
                logger.LogDebug("Authenticode trust verification failed for {File}.", filePath);
                return false;
            }

            if (!IsMicrosoftSigner(filePath))
            {
                logger.LogDebug("Authenticode signer for {File} is not Microsoft.", filePath);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Authenticode verification of {File} threw; treating as untrusted.", filePath);
            return false;
        }
    }

    private static bool VerifyTrust(string filePath, ILogger logger)
    {
        // Prefer full-chain revocation using locally cached CRLs only (no network fetch, so a
        // locked-down/offline environment does not hang). A definitively revoked certificate is a hard
        // failure; when revocation data simply cannot be obtained offline, fall back to a signature-only
        // check so the gate still works — the signature and Microsoft-signer checks remain in force.
        var hr = VerifyTrustCore(filePath, WTD_REVOKE_WHOLECHAIN, WTD_CACHE_ONLY_URL_RETRIEVAL);
        if (hr == 0)
        {
            return true;
        }

        if (hr is CERT_E_REVOKED)
        {
            logger.LogDebug("Authenticode certificate for {File} is revoked.", filePath);
            return false;
        }

        if (hr is CERT_E_REVOCATION_FAILURE or CRYPT_E_REVOCATION_OFFLINE or CRYPT_E_NO_REVOCATION_CHECK)
        {
            logger.LogDebug("Revocation data unavailable for {File} (0x{Hr:X8}); falling back to signature-only verification.", filePath, hr);
            return VerifyTrustCore(filePath, WTD_REVOKE_NONE, WTD_REVOCATION_CHECK_NONE) == 0;
        }

        return false;
    }

    private static int VerifyTrustCore(string filePath, uint revocationChecks, uint provFlags)
    {
        var pPath = Marshal.StringToHGlobalUni(filePath);
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = pPath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = revocationChecks,
                dwUnionChoice = WTD_CHOICE_FILE,
                pInfo = (IntPtr)(&fileInfo),
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = provFlags,
            };

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // Always release the state data, regardless of the verify outcome.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pPath);
        }
    }

    private static bool IsMicrosoftSigner(string filePath)
    {
        // CreateFromSignedFile returns the Authenticode signer certificate. There is no
        // non-obsolete replacement for extracting a signer from a signed file (X509CertificateLoader
        // only loads raw certificate blobs), so the SYSLIB0057 obsoletion is suppressed here.
#pragma warning disable SYSLIB0057
        var subject = X509Certificate.CreateFromSignedFile(filePath).Subject;
#pragma warning restore SYSLIB0057
        return IsMicrosoftSubject(subject);
    }

    /// <summary>
    /// Returns <c>true</c> when an X.509 subject distinguished name identifies Microsoft as the signer.
    /// Extracted for unit testing the signer-identity gate independently of the native trust check.
    /// </summary>
    internal static bool IsMicrosoftSubject(string subject) =>
        subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
        || subject.Contains("CN=Microsoft", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}

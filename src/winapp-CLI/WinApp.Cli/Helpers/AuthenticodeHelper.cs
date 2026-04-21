// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Verifies Authenticode signatures on PE files and MSIX packages using WinVerifyTrust.
/// </summary>
internal static class AuthenticodeHelper
{
    // WINTRUST_ACTION_GENERIC_VERIFY_V2 — standard Authenticode verification
    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    private const string ExpectedSignerName = "Microsoft Corporation";

    /// <summary>
    /// Verifies the file has a valid Authenticode signature from Microsoft Corporation.
    /// Returns a result indicating success or the specific failure reason.
    /// </summary>
    public static unsafe SignatureVerificationResult VerifyMicrosoftSignature(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return SignatureVerificationResult.Fail("File not found.");
        }

        // Step 1: Verify the Authenticode signature chain using WinVerifyTrust
        int winVerifyResult;
        fixed (char* pFilePath = filePath)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = pFilePath,
            };

            var trustData = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = &fileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
            };

            var actionId = WintrustActionGenericVerifyV2;
            winVerifyResult = WinVerifyTrust(IntPtr.Zero, &actionId, &trustData);

            // Close the verification state handle
            trustData.dwStateAction = WTD_STATEACTION_CLOSE;
            _ = WinVerifyTrust(IntPtr.Zero, &actionId, &trustData);
        }

        if (winVerifyResult != 0)
        {
            return SignatureVerificationResult.Fail(
                $"Authenticode signature verification failed (HRESULT: 0x{winVerifyResult:X8}). " +
                "The file may be unsigned, tampered with, or signed by an untrusted certificate.");
        }

        // Step 2: Extract the signing certificate and verify the signer is Microsoft
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is obsolete — no modern .NET replacement exists
            using var baseCert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var cert = new X509Certificate2(baseCert);
            var signerName = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            if (!string.Equals(signerName, ExpectedSignerName, StringComparison.OrdinalIgnoreCase))
            {
                return SignatureVerificationResult.Fail(
                    $"File is signed by '{signerName}', expected '{ExpectedSignerName}'.");
            }

            return SignatureVerificationResult.Success(signerName);
        }
        catch (Exception ex)
        {
            return SignatureVerificationResult.Fail(
                $"Failed to extract signing certificate: {ex.Message}");
        }
    }

    // ── P/Invoke declarations for WinVerifyTrust (wintrust.dll) ──

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern unsafe int WinVerifyTrust(
        IntPtr hwnd,
        Guid* pgActionID,
        WINTRUST_DATA* pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    // Layout matches the native WINTRUST_DATA struct on both x64 and ARM64.
    // The union field (pFile/pCatalog/pBlob/pSgnr/pCert) is represented as a
    // single WINTRUST_FILE_INFO* since we only use WTD_CHOICE_FILE.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}

internal readonly struct SignatureVerificationResult
{
    public bool IsValid { get; }
    public string? SignerName { get; }
    public string? ErrorMessage { get; }

    private SignatureVerificationResult(bool isValid, string? signerName, string? errorMessage)
    {
        IsValid = isValid;
        SignerName = signerName;
        ErrorMessage = errorMessage;
    }

    public static SignatureVerificationResult Success(string signerName) => new(true, signerName, null);
    public static SignatureVerificationResult Fail(string errorMessage) => new(false, null, errorMessage);
}

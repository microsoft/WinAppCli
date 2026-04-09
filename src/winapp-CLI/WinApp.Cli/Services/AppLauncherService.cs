// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace WinApp.Cli.Services;

internal class AppLauncherService(ILogger<AppLauncherService> logger) : IAppLauncherService
{
    // Crockford's Base32 alphabet (used by Windows for publisher ID)
    private static readonly char[] Base32Chars = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    /// <inheritdoc />
    [SupportedOSPlatform("windows8.0")]
    public uint LaunchByAumid(string aumid, string? arguments = null)
    {
        var aam = ApplicationActivationManager.CreateInstance<IApplicationActivationManager>();
        aam.ActivateApplication(aumid, arguments ?? string.Empty, ACTIVATEOPTIONS.AO_NONE, out uint pid);
        return pid;
    }

    /// <inheritdoc />
    public string ComputePackageFamilyName(string packageName, string publisher)
    {
        // Windows uses the first 13 characters of a Crockford Base32 encoding
        // of the first 8 bytes of the SHA256 hash of the publisher DN (UTF-16LE, uppercase)
        var publisherId = ComputePublisherId(publisher);
        return $"{packageName}_{publisherId}";
    }

    /// <inheritdoc />
    public string? GetPackageFullName(string packageFamilyName)
    {
        try
        {
            var pm = new PackageManager();
            var packages = pm.FindPackages(packageFamilyName);
            return packages.FirstOrDefault()?.Id.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the publisher ID from the publisher DN.
    /// The publisher ID is a 13-character Crockford Base32 encoding
    /// of the first 8 bytes of the SHA256 hash of the publisher DN (UTF-16LE).
    /// </summary>
    private static string ComputePublisherId(string publisher)
    {
        // Encode publisher as UTF-16LE (no case conversion - Windows uses the exact string)
        var publisherBytes = Encoding.Unicode.GetBytes(publisher);

        // Compute SHA256 hash
        var hashBytes = SHA256.HashData(publisherBytes);

        // Take first 8 bytes (64 bits) and encode as Crockford Base32
        // 64 bits = 13 Base32 characters (65 bits capacity, last bit unused)
        return EncodeBase32Crockford(hashBytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Encodes bytes using Crockford's Base32 alphabet.
    /// For 8 bytes (64 bits), produces exactly 13 characters.
    /// </summary>
    private static string EncodeBase32Crockford(ReadOnlySpan<byte> data)
    {
        // For 8 bytes (64 bits), we need 13 characters (65 bits / 5 bits per char)
        // We pad with 1 zero bit on the right to get 65 bits
        var result = new char[13];

        // Process 64 bits from 8 bytes into a ulong (MSB first)
        ulong bits = 0;
        foreach (byte b in data)
        {
            bits = (bits << 8) | b;
        }

        // Extract 13 groups of 5 bits each, reading from MSB to LSB
        // First 12 groups: 5 bits each from the 64 bits
        // Last group: remaining 4 bits shifted left by 1 (padded with 0)
        for (int i = 0; i < 13; i++)
        {
            int index;
            if (i < 12)
            {
                // Extract 5 bits starting from bit position (63 - i*5) down to (59 - i*5)
                int shift = 59 - (i * 5);
                index = (int)((bits >> shift) & 0x1F);
            }
            else
            {
                // Last group: only 4 bits remaining (bits 3-0), pad with 0 on the right
                index = (int)((bits & 0xF) << 1);
            }
            result[i] = Base32Chars[index];
        }

        return new string(result).ToLowerInvariant();
    }

    /// <inheritdoc />
    [SupportedOSPlatform("windows8.0")]
    public void TerminatePackageProcesses(string? packageFullName, uint processId)
    {
        if (packageFullName is not null)
        {
            try
            {
                var debugSettings = PackageDebugSettings.CreateInstance<IPackageDebugSettings>();
                debugSettings.TerminateAllProcesses(packageFullName);
                logger.LogDebug("Terminated all processes for package {PackageFullName}.", packageFullName);
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug("IPackageDebugSettings.TerminateAllProcesses failed: {Message}. Falling back to PID-based kill.", ex.Message);
            }
        }

        // Fallback: kill the specific process by PID
        if (processId == 0 || processId > int.MaxValue)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            process.Kill(entireProcessTree: true);
            logger.LogDebug("Terminated process tree for PID {PID}.", processId);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }
}

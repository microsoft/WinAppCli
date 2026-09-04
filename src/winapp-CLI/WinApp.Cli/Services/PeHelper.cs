// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection.PortableExecutable;
using System.Buffers.Binary;

namespace WinApp.Cli.Services;

/// <summary>
/// Static helper for detecting executable architecture from PE headers.
/// </summary>
internal static class PeHelper
{
    // Official apphost bundle marker from dotnet/runtime's bundle_marker.c. The 8 bytes immediately
    // before this signature are patched from zero to the bundle-header offset by single-file publish.
    private static ReadOnlySpan<byte> DotNetBundleSignature =>
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    /// <summary>
    /// Detects the architecture of a PE file and returns an MSIX-style architecture string:
    /// "x86", "x64", "arm", "arm64", or "neutral".
    ///
    /// Rules:
    /// - Native PE images are classified from the COFF Machine field.
    /// - Managed .NET IL-only images are classified using COR flags:
    ///     * I386 + ILOnly + Requires32Bit => x86
    ///     * I386 + ILOnly + !Requires32Bit => neutral
    /// - Mixed-mode / native-hosted managed images fall back to the native Machine field.
    ///
    /// Returns null if the file is not a valid PE image or uses an unsupported architecture.
    /// </summary>
    internal static string? DetectPeArchitecture(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var peReader = new PEReader(stream);

            var headers = peReader.PEHeaders;
            var coff = headers.CoffHeader;
            var cor = headers.CorHeader;

            ushort machine = (ushort)coff.Machine;

            return ClassifyArchitecture(machine, cor?.Flags);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the embedded bundle-header offset when <paramref name="filePath"/> is an official .NET
    /// single-file apphost, or <see langword="null"/> for an ordinary apphost/native executable.
    /// File access failures propagate so security-sensitive callers can fail closed.
    /// </summary>
    internal static long? GetDotNetSingleFileBundleHeaderOffset(string filePath)
    {
        const int offsetSize = sizeof(long);
        var signature = DotNetBundleSignature;
        var overlapLength = offsetSize + signature.Length - 1;
        var buffer = new byte[64 * 1024 + overlapLength];
        var carry = 0;
        long bufferStart = 0;

        using var stream = File.OpenRead(filePath);
        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            var total = carry + read;
            if (total < offsetSize + signature.Length && read == 0)
            {
                return null;
            }

            for (var signatureIndex = offsetSize;
                 signatureIndex <= total - signature.Length;
                 signatureIndex++)
            {
                if (!buffer.AsSpan(signatureIndex, signature.Length).SequenceEqual(signature))
                {
                    continue;
                }

                var headerOffset = BinaryPrimitives.ReadInt64LittleEndian(
                    buffer.AsSpan(signatureIndex - offsetSize, offsetSize));
                var markerOffset = bufferStart + signatureIndex - offsetSize;
                if (headerOffset > markerOffset && headerOffset < stream.Length)
                {
                    return headerOffset;
                }
            }

            if (read == 0)
            {
                return null;
            }

            carry = Math.Min(overlapLength, total);
            buffer.AsSpan(total - carry, carry).CopyTo(buffer);
            bufferStart += total - carry;
        }
    }

    /// <summary>
    /// Maps a COFF machine value and optional COR flags to an MSIX-style architecture string.
    /// Extracted as a pure function (no file I/O) so every native/managed/mixed-mode branch can be
    /// unit tested directly.
    /// <list type="bullet">
    /// <item>When <paramref name="corFlags"/> is <c>null</c> the image is native/mixed-mode and is
    /// classified from <paramref name="machine"/>.</item>
    /// <item>Managed IL-only images use the COR flags: I386 + Requires32Bit =&gt; x86, otherwise
    /// I386 =&gt; neutral (AnyCPU).</item>
    /// </list>
    /// Returns <c>null</c> for unsupported architectures.
    /// </summary>
    internal static string? ClassifyArchitecture(ushort machine, CorFlags? corFlags)
    {
        // Native or mixed-mode case: use the PE machine directly.
        if (corFlags is null)
        {
            return MapNativeMachine(machine);
        }

        CorFlags flags = corFlags.Value;
        bool isIlOnly = (flags & CorFlags.ILOnly) != 0;
        bool requires32Bit = (flags & CorFlags.Requires32Bit) != 0;

        // Managed IL-only assemblies need special handling.
        // In particular, IL-only I386 without Requires32Bit is effectively AnyCPU/neutral.
        if (isIlOnly)
        {
            return machine switch
            {
                0x014C => requires32Bit ? "x86" : "neutral", // I386
                0x8664 => "x64",   // unusual for pure IL-only, but valid to preserve
                0x01C0 => "arm",   // ARM
                0x01C4 => "arm",   // ARMNT
                0xAA64 => "arm64", // ARM64
                _ => null
            };
        }

        // Mixed-mode / native-entry managed image: machine matters.
        return MapNativeMachine(machine);
    }

    private static string? MapNativeMachine(ushort machine) => machine switch
    {
        0x014C => "x86",    // IMAGE_FILE_MACHINE_I386
        0x8664 => "x64",    // IMAGE_FILE_MACHINE_AMD64
        0xAA64 => "arm64",  // IMAGE_FILE_MACHINE_ARM64
        0x01C4 => "arm",    // IMAGE_FILE_MACHINE_ARMNT
        0x01C0 => "arm",    // IMAGE_FILE_MACHINE_ARM
        _ => null
    };
}

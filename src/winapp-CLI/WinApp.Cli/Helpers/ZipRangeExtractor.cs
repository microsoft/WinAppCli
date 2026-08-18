// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Compression;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Random-access byte source addressed by absolute offset. Implementations may be backed by an
/// in-memory buffer (tests) or HTTP range requests against a remote archive (production).
/// </summary>
internal interface IRangeReader
{
    /// <summary>Total length of the underlying resource in bytes.</summary>
    Task<long> GetLengthAsync(CancellationToken cancellationToken);

    /// <summary>Reads exactly <paramref name="length"/> bytes starting at <paramref name="offset"/>.</summary>
    Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken);
}

/// <summary>A single central-directory entry resolved from a ZIP (with ZIP64 fields applied).</summary>
/// <param name="Name">Entry path using forward slashes.</param>
/// <param name="Method">Compression method (0 = stored, 8 = deflate).</param>
/// <param name="CompressedSize">Size of the entry's data in the archive.</param>
/// <param name="UncompressedSize">Size after decompression.</param>
/// <param name="LocalHeaderOffset">Absolute offset (in the reader) of the entry's local file header.</param>
internal sealed record ZipEntry(string Name, ushort Method, long CompressedSize, long UncompressedSize, long LocalHeaderOffset);

/// <summary>
/// Parses a ZIP (including ZIP64) central directory and extracts individual entries using only
/// ranged reads — never downloading the whole archive. Supports nested archives (e.g. a STORED
/// inner <c>.msix</c> inside an <c>.msixbundle</c>) by treating each as a sub-range with its own
/// base offset.
/// </summary>
internal static class ZipRangeExtractor
{
    private const uint EocdSignature = 0x06054b50;       // PK\x05\x06
    private const uint Zip64LocatorSignature = 0x07064b50; // PK\x06\x07
    private const uint Zip64EocdSignature = 0x06064b50;  // PK\x06\x06
    private const uint CentralHeaderSignature = 0x02014b50; // PK\x01\x02
    private const int MaxEocdSearch = 65557; // 22-byte EOCD + 64KiB max comment
    private const int MinEocdSize = 22;      // signature through the comment-length field
    private const int MinCentralHeaderSize = 46; // fixed part of one central-directory header
    private const int MaxDirectoryProbes = 8;    // reads outside the tail spent confirming candidates
    private const uint Zip64Marker = 0xFFFFFFFF;
    private const ushort Zip64ExtraId = 0x0001;

    /// <summary>
    /// Locates the central directory for an archive whose bytes occupy
    /// <c>[archiveBase, archiveBase + archiveSize)</c> within <paramref name="reader"/>.
    /// </summary>
    /// <returns>The absolute offset and size of the central directory.</returns>
    public static async Task<(long Offset, long Size)> FindCentralDirectoryAsync(
        IRangeReader reader, long archiveBase, long archiveSize, CancellationToken cancellationToken)
    {
        var tailLen = (int)Math.Min(MaxEocdSearch, archiveSize);
        var tailStart = archiveBase + archiveSize - tailLen;
        var tail = await reader.ReadAsync(tailStart, tailLen, cancellationToken);

        // A legal comment can hold a complete fake record, so a candidate is only accepted once the
        // directory it points at is confirmed to be one.
        (long Offset, long Size)? emptyFallback = null;
        var probes = 0;

        for (var eocd = NextEocdCandidate(tail, tail.Length); eocd >= 0; eocd = NextEocdCandidate(tail, eocd))
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
            long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
            long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));

            if (cdOffset == Zip64Marker || cdSize == Zip64Marker || count == 0xFFFF)
            {
                var zip64 = await TryReadZip64DirectoryAsync(
                    reader, archiveBase, archiveSize, tail, tailStart, eocd, cancellationToken);
                var charged = zip64.Probed;

                if (zip64.Ok)
                {
                    // A comment can carry a forged locator and record too, so the resolved directory
                    // gets the same confirmation, anchored on the record rather than the EOCD.
                    var (isZip64Directory, zip64Probed) = await IsCentralDirectoryAsync(
                        reader, archiveBase, archiveSize, zip64.Offset, zip64.Size, zip64.RecordOffset, tail, tailStart, cancellationToken);
                    if (isZip64Directory)
                    {
                        return (archiveBase + zip64.Offset, zip64.Size);
                    }

                    charged |= zip64Probed;
                }

                // Only reads that actually left the tail count, or a comment holding a handful of
                // ZIP64-shaped records would exhaust the budget and reject a valid archive.
                if (charged && ++probes >= MaxDirectoryProbes)
                {
                    break;
                }

                continue;
            }

            // A record declaring no directory has nothing to confirm, and a zeroed record planted in a
            // comment looks exactly like one. Require it to sit where an empty directory would, and let
            // an earlier candidate replace a later one so the comment cannot win.
            if (count == 0 && cdSize == 0)
            {
                if (archiveBase + cdOffset == tailStart + eocd)
                {
                    emptyFallback = (archiveBase + cdOffset, 0);
                }

                continue;
            }

            // In a ZIP32 archive the directory runs right up to the record describing it.
            var (isDirectory, probed) = await IsCentralDirectoryAsync(
                reader, archiveBase, archiveSize, cdOffset, cdSize, tailStart + eocd, tail, tailStart, cancellationToken);
            if (isDirectory)
            {
                return (archiveBase + cdOffset, cdSize);
            }

            // Every candidate that points outside the tail costs a round trip on the HTTP reader, and a
            // 64 KiB comment can hold thousands of them. A real archive needs one.
            if (probed && ++probes >= MaxDirectoryProbes)
            {
                break;
            }
        }

        return emptyFallback ?? throw new InvalidDataException("End-of-central-directory record not found.");
    }

    /// <summary>
    /// Confirms a candidate's declared central directory is inside the archive, is large enough to hold
    /// a header, ends where the record describing it begins, and actually starts with one.
    /// </summary>
    /// <returns>Whether it is a directory, and whether confirming it cost a read outside the tail.</returns>
    private static async Task<(bool IsDirectory, bool Probed)> IsCentralDirectoryAsync(
        IRangeReader reader, long archiveBase, long archiveSize, long cdOffset, long cdSize, long mustEndAt,
        byte[] tail, long tailStart, CancellationToken cancellationToken)
    {
        // Anything shorter than one header cannot be the directory a non-empty record claims, which is
        // what a comment-planted record uses to pass a signature-only check.
        if (cdOffset < 0 || cdSize < MinCentralHeaderSize || cdOffset > archiveSize - cdSize)
        {
            return (false, false);
        }

        if (archiveBase + cdOffset + cdSize != mustEndAt)
        {
            return (false, false);
        }

        var head = archiveBase + cdOffset;
        if (head >= tailStart && head + 4 <= tailStart + tail.Length)
        {
            var local = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan((int)(head - tailStart)));
            return (local == CentralHeaderSignature, false);
        }

        var bytes = await reader.ReadAsync(head, 4, cancellationToken);
        return (BinaryPrimitives.ReadUInt32LittleEndian(bytes) == CentralHeaderSignature, true);
    }

    /// <summary>
    /// Resolves the directory a marker-bearing record points at, via its ZIP64 locator and record.
    /// </summary>
    /// <returns>
    /// Whether it resolved, the directory location, the record's offset, and whether resolving cost a
    /// read outside the tail. Failures are reported rather than thrown, because a candidate drawn from
    /// a comment failing to resolve is ordinary and the scan continues past it.
    /// </returns>
    private static async Task<(bool Ok, long Offset, long Size, long RecordOffset, bool Probed)> TryReadZip64DirectoryAsync(
        IRangeReader reader, long archiveBase, long archiveSize, byte[] tail, long tailStart, int eocd, CancellationToken cancellationToken)
    {
        var locator = LastIndexOfSignature(tail.AsSpan(0, eocd), Zip64LocatorSignature);
        if (locator < 0)
        {
            return (false, 0, 0, 0, false);
        }

        // Offset of the ZIP64 EOCD record, relative to the archive's base. Attacker-controlled, and
        // passing it straight to the reader turns a malformed archive into an out-of-range or
        // transport error instead of a rejection.
        var recordRelative = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8));
        if (recordRelative < 0 || recordRelative > archiveSize - 56)
        {
            return (false, 0, 0, 0, false);
        }

        var recordAbsolute = archiveBase + recordRelative;

        byte[] record;
        int recordPos;
        var probed = false;
        if (recordAbsolute >= tailStart && recordAbsolute + 56 <= tailStart + tail.Length)
        {
            record = tail;
            recordPos = (int)(recordAbsolute - tailStart);
        }
        else
        {
            record = await reader.ReadAsync(recordAbsolute, 56, cancellationToken);
            recordPos = 0;
            probed = true;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(recordPos)) != Zip64EocdSignature)
        {
            return (false, 0, 0, 0, probed);
        }

        var cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(recordPos + 40));
        var cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(recordPos + 48));
        return (true, cdOffset, cdSize, recordAbsolute, probed);
    }

    /// <summary>
    /// Parses the raw central-directory bytes into entries. <paramref name="archiveBase"/> is added
    /// to each entry's (archive-relative) local-header offset to produce an absolute reader offset.
    /// </summary>
    public static IReadOnlyList<ZipEntry> ParseCentralDirectory(byte[] centralDirectory, long archiveBase)
    {
        var entries = new List<ZipEntry>();
        var p = 0;
        while (p + 46 <= centralDirectory.Length &&
               BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(p)) == CentralHeaderSignature)
        {
            var method = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(p + 10));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(p + 20));
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(p + 24));
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(p + 28));
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(p + 30));
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(p + 32));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(p + 42));

            // The three lengths are attacker-controlled; without this the slices below read past the
            // buffer and surface as ArgumentOutOfRangeException instead of a rejected archive.
            if ((long)p + 46 + nameLen + extraLen + commentLen > centralDirectory.Length)
            {
                throw new InvalidDataException(
                    $"Central-directory entry at offset {p} declares {nameLen + extraLen + commentLen} bytes of name/extra/comment, which extends past the {centralDirectory.Length}-byte directory.");
            }

            var name = System.Text.Encoding.UTF8.GetString(centralDirectory, p + 46, nameLen);
            var extra = centralDirectory.AsSpan(p + 46 + nameLen, extraLen);

            if (uncompressed == Zip64Marker || compressed == Zip64Marker || localOffset == Zip64Marker)
            {
                ApplyZip64Extra(extra, ref uncompressed, ref compressed, ref localOffset);
            }

            entries.Add(new ZipEntry(name.Replace('\\', '/'), method, compressed, uncompressed, archiveBase + localOffset));
            p += 46 + nameLen + extraLen + commentLen;
        }

        return entries;
    }

    private static void ApplyZip64Extra(ReadOnlySpan<byte> extra, ref long uncompressed, ref long compressed, ref long localOffset)
    {
        var p = 0;
        while (p + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[p..]);
            var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(extra[(p + 2)..]);
            var q = p + 4;
            if (id == Zip64ExtraId)
            {
                // Fields appear in this fixed order, but only those whose 32-bit value was 0xFFFFFFFF.
                if (uncompressed == Zip64Marker && q + 8 <= extra.Length) { uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra[q..]); q += 8; }
                if (compressed == Zip64Marker && q + 8 <= extra.Length) { compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra[q..]); q += 8; }
                if (localOffset == Zip64Marker && q + 8 <= extra.Length) { localOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra[q..]); }
                return;
            }

            p += 4 + dataLen;
        }
    }

    /// <summary>Computes the absolute offset where an entry's (compressed) data begins.</summary>
    public static async Task<long> GetDataStartAsync(IRangeReader reader, ZipEntry entry, CancellationToken cancellationToken)
    {
        // Local file headers carry their own name/extra lengths, which may differ from the central
        // directory's, so the data start must be derived from the local header itself.
        var header = await reader.ReadAsync(entry.LocalHeaderOffset, 30, cancellationToken);
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        return entry.LocalHeaderOffset + 30 + nameLen + extraLen;
    }

    /// <summary>Reads and decompresses a single entry's bytes (supports stored and deflate).</summary>
    public static async Task<byte[]> ExtractEntryAsync(IRangeReader reader, ZipEntry entry, CancellationToken cancellationToken)
    {
        if (entry.CompressedSize > int.MaxValue)
        {
            throw new InvalidDataException($"Entry '{entry.Name}' is too large to extract ({entry.CompressedSize} bytes).");
        }

        var dataStart = await GetDataStartAsync(reader, entry, cancellationToken);
        var compressed = await reader.ReadAsync(dataStart, (int)entry.CompressedSize, cancellationToken);

        return entry.Method switch
        {
            0 => compressed,
            8 => Inflate(compressed, entry.UncompressedSize),
            _ => throw new NotSupportedException($"Unsupported ZIP compression method {entry.Method} for '{entry.Name}'."),
        };
    }

    private static byte[] Inflate(byte[] compressed, long expectedSize)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = expectedSize is > 0 and <= int.MaxValue
            ? new MemoryStream((int)expectedSize)
            : new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Returns the next end-of-central-directory candidate strictly before <paramref name="before"/>,
    /// scanning backward, or -1.
    /// </summary>
    /// <remarks>
    /// A candidate must have its full fixed fields and a declared comment length that runs exactly to
    /// the end of the archive. That is necessary but not sufficient: comment bytes can satisfy it too,
    /// so the caller still has to confirm the directory the candidate points at.
    /// </remarks>
    private static int NextEocdCandidate(ReadOnlySpan<byte> tail, int before)
    {
        var start = Math.Min(before - 1, tail.Length - MinEocdSize);

        for (var i = start; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.Slice(i)) != EocdSignature)
            {
                continue;
            }

            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(i + 20));
            if (i + MinEocdSize + commentLen == tail.Length)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndexOfSignature(ReadOnlySpan<byte> buffer, uint signature)
    {
        Span<byte> needle = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, signature);
        for (var i = buffer.Length - 4; i >= 0; i--)
        {
            if (buffer[i] == needle[0] && buffer[i + 1] == needle[1] &&
                buffer[i + 2] == needle[2] && buffer[i + 3] == needle[3])
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>An <see cref="IRangeReader"/> backed by an in-memory buffer (used by tests).</summary>
internal sealed class MemoryRangeReader(byte[] data) : IRangeReader
{
    public Task<long> GetLengthAsync(CancellationToken cancellationToken) => Task.FromResult((long)data.Length);

    public Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken)
    {
        // Written to avoid offset + length, which overflows for a ZIP64-sized offset and would let the
        // read through to Array.Copy.
        if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"Range of {length} bytes at offset {offset} is outside the buffer ({data.Length}).");
        }

        var slice = new byte[length];
        Array.Copy(data, offset, slice, 0, length);
        return Task.FromResult(slice);
    }
}

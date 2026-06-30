// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Acquires <c>JsProvider.dll</c> (the WinDbg JavaScript scripting host required by
/// <c>.scriptload</c>) for the host architecture. The DLL is not distributed on NuGet — it ships
/// only inside the WinDbg <c>.msixbundle</c> — so it is extracted directly from the official
/// download using HTTP range requests, reading only the few hundred kilobytes needed rather than
/// the ~772&#160;MB bundle.
/// </summary>
/// <remarks>
/// The bundle is a ZIP64 archive whose per-architecture inner <c>.msix</c> packages are STORED
/// (uncompressed), so the inner archive can be addressed as a contiguous sub-range and parsed in
/// place. <c>JsProvider.dll</c> inside the inner msix is deflate-compressed and is inflated after
/// extraction. See <see cref="ZipRangeExtractor"/> for the underlying range-based ZIP reader.
/// </remarks>
internal static class WinDbgJsProviderAcquirer
{
    // Stable entry point published by the WinDbg team; resolves to the current MainBundle URI.
    private const string AppInstallerUrl = "https://windbg.download.prss.microsoft.com/dbazure/prod/1-0-0/windbg.appinstaller";

    // Pinned fallback used when the appinstaller cannot be parsed (kept reasonably current).
    private const string FallbackBundleUrl = "https://windbg.download.prss.microsoft.com/dbazure/prod/1-2603-20001-0/windbg.msixbundle";

    private const string TargetFileName = "JsProvider.dll";
    private const int MaxReadAttempts = 5;

    /// <summary>
    /// Attempts to download <c>JsProvider.dll</c> into <paramref name="destDir"/> (next to the
    /// debugging engine). Returns <c>true</c> on success; failures are logged and swallowed so the
    /// triage pass can degrade gracefully.
    /// </summary>
    public static async Task<bool> TryAcquireAsync(DirectoryInfo destDir, ILogger logger, CancellationToken cancellationToken)
    {
        var (msixName, pathPrefix) = HostTokens(RuntimeInformation.ProcessArchitecture);
        if (msixName == null || pathPrefix == null)
        {
            logger.LogDebug("JsProvider acquisition skipped: unsupported host architecture {Arch}.", RuntimeInformation.ProcessArchitecture);
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bundleUrl = await ResolveBundleUrlAsync(http, logger, cancellationToken);

            var reader = new HttpRangeReader(http, bundleUrl);
            var bytes = await ExtractJsProviderAsync(reader, msixName, pathPrefix, cancellationToken);
            if (bytes == null)
            {
                logger.LogDebug("JsProvider.dll not found in WinDbg bundle for {Msix}/{Prefix}.", msixName, pathPrefix);
                return false;
            }

            Directory.CreateDirectory(destDir.FullName);
            var targetPath = Path.Combine(destDir.FullName, TargetFileName);
            await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
            logger.LogDebug("Acquired {File} ({Size} bytes) from WinDbg bundle into {Dir}.", TargetFileName, bytes.Length, destDir.FullName);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to acquire {File} from the WinDbg bundle.", TargetFileName);
            return false;
        }
    }

    /// <summary>
    /// Extracts the host-architecture <c>JsProvider.dll</c> bytes from a WinDbg bundle exposed via
    /// <paramref name="reader"/>, or <c>null</c> when the expected entries are absent. Validates that
    /// the result is a PE image. This is the unit-testable core (no network dependency).
    /// </summary>
    public static async Task<byte[]?> ExtractJsProviderAsync(
        IRangeReader reader, string msixName, string pathPrefix, CancellationToken cancellationToken)
    {
        var total = await reader.GetLengthAsync(cancellationToken);

        var (cdOffset, cdSize) = await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, total, cancellationToken);
        var bundleEntries = ZipRangeExtractor.ParseCentralDirectory(
            await reader.ReadAsync(cdOffset, checked((int)cdSize), cancellationToken), 0);

        var inner = bundleEntries.FirstOrDefault(e => e.Name.Equals(msixName, StringComparison.OrdinalIgnoreCase));
        if (inner == null)
        {
            return null;
        }

        var innerStart = await ZipRangeExtractor.GetDataStartAsync(reader, inner, cancellationToken);
        var (innerCdOffset, innerCdSize) = await ZipRangeExtractor.FindCentralDirectoryAsync(
            reader, innerStart, inner.CompressedSize, cancellationToken);
        var innerEntries = ZipRangeExtractor.ParseCentralDirectory(
            await reader.ReadAsync(innerCdOffset, checked((int)innerCdSize), cancellationToken), innerStart);

        // Prefer the self-contained provider (<arch>/winext/JsProvider.dll) over the chakra variant.
        var target = $"{pathPrefix}/winext/{TargetFileName}";
        var js = innerEntries.FirstOrDefault(e => e.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (js == null)
        {
            return null;
        }

        var bytes = await ZipRangeExtractor.ExtractEntryAsync(reader, js, cancellationToken);
        if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new InvalidDataException($"Extracted '{target}' is not a valid PE image.");
        }

        return bytes;
    }

    /// <summary>
    /// Maps a process architecture to the WinDbg inner-msix file name and the architecture folder
    /// prefix used inside that msix. Returns <c>(null, null)</c> for unsupported architectures.
    /// </summary>
    public static (string? MsixName, string? PathPrefix) HostTokens(Architecture architecture) => architecture switch
    {
        Architecture.X64 => ("windbg_win-x64.msix", "amd64"),
        Architecture.Arm64 => ("windbg_win-arm64.msix", "arm64"),
        Architecture.X86 => ("windbg_win-x86.msix", "x86"),
        _ => (null, null),
    };

    private static async Task<string> ResolveBundleUrlAsync(HttpClient http, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await http.GetStringAsync(AppInstallerUrl, cancellationToken);
            var doc = XDocument.Parse(xml);
            var uri = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "MainBundle")?
                .Attribute("Uri")?.Value;

            if (!string.IsNullOrWhiteSpace(uri))
            {
                logger.LogDebug("Resolved WinDbg bundle URI from appinstaller: {Uri}", uri);
                return uri;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to resolve WinDbg bundle URI from appinstaller; using pinned fallback.");
        }

        return FallbackBundleUrl;
    }

    /// <summary>An <see cref="IRangeReader"/> backed by HTTP range requests with retry-on-transient.</summary>
    private sealed class HttpRangeReader(HttpClient http, string url) : IRangeReader
    {
        private long? _length;

        public async Task<long> GetLengthAsync(CancellationToken cancellationToken)
        {
            if (_length is { } cached)
            {
                return cached;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentRange?.Length
                ?? throw new InvalidOperationException("Server did not report a total length for ranged requests.");
            _length = total;
            return total;
        }

        public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new HttpRequestException($"Expected 206 PartialContent, got {(int)response.StatusCode} for range {offset}-{offset + length - 1}.");
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length != length)
                    {
                        throw new IOException($"Short range read: requested {length} bytes, got {bytes.Length}.");
                    }

                    return bytes;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                }
            }

            throw new IOException($"Failed to read range {offset}-{offset + length - 1} after {MaxReadAttempts} attempts.", last);
        }
    }
}

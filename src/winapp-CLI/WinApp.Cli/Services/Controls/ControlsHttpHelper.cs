// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Net.Http;

/// <summary>
/// Shared HTTP helper for the find-ui corpus fetchers. Every upstream read goes
/// through here so a compromised or accidentally-huge file in
/// <c>microsoft/WinUI-Gallery</c> or <c>CommunityToolkit/Windows</c> cannot
/// exhaust memory: <see cref="HttpClient.GetStringAsync(string)"/> buffers the
/// whole body unbounded, so we stream with a hard byte cap and fail closed.
/// </summary>
internal static class ControlsHttpHelper
{
    /// <summary>
    /// Maximum response size, in bytes, for any single upstream fetch. The
    /// largest documents currently served by either upstream sit under ~1 MB;
    /// 16 MB leaves generous headroom while still preventing runaway reads.
    /// </summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Like <see cref="HttpClient.GetStringAsync(string, CancellationToken)"/>,
    /// but streams the body and throws <see cref="HttpRequestException"/> if it
    /// exceeds <see cref="MaxResponseBytes"/>.
    /// </summary>
    public static async Task<string> GetStringCappedAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Like <see cref="GetStringCappedAsync"/> but returns <c>null</c> on any
    /// non-success status or transport error instead of throwing — for
    /// best-effort per-sample fetches where a single missing file must not abort
    /// the whole refresh.
    /// </summary>
    public static async Task<string?> TryGetStringCappedAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<string> ReadCappedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes)
        {
            throw new HttpRequestException(
                $"Response body advertised {declared} bytes, exceeding the find-ui fetch cap of {MaxResponseBytes} bytes.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new HttpRequestException(
                    $"Response body exceeded the find-ui fetch cap of {MaxResponseBytes} bytes.");
            }
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

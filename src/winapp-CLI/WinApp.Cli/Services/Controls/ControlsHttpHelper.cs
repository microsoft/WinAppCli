// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http;

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Shared HTTP utilities for the controls fetchers.
///
/// All upstream fetches go through here so we can enforce a maximum response
/// size — a compromised or accidentally-huge upstream file in
/// <c>microsoft/WinUI-Gallery</c> or <c>CommunityToolkit/Windows</c> would
/// otherwise be able to OOM the CLI by serving an unbounded payload to
/// <see cref="HttpClient.GetStringAsync(string)"/>.
/// </summary>
internal static class ControlsHttpHelper
{
    /// <summary>
    /// Maximum response size, in bytes, for any single upstream fetch. The
    /// largest documents currently served by either upstream sit under 1 MB;
    /// 10 MB leaves plenty of headroom while still preventing runaway reads.
    /// </summary>
    public const long MaxResponseBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Like <see cref="HttpClient.GetStringAsync(string)"/>, but throws
    /// <see cref="HttpRequestException"/> if the body exceeds
    /// <see cref="MaxResponseBytes"/>.
    /// </summary>
    public static async Task<string> GetStringWithLimitAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsLimitedStringAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Like <see cref="HttpClient.GetAsync(string)"/> but the caller MUST read
    /// the body via <see cref="ReadAsLimitedStringAsync"/> so the
    /// <see cref="MaxResponseBytes"/> cap is enforced.
    /// </summary>
    public static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        return client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Reads the response body as a string, enforcing the size cap.</summary>
    public static async Task<string> ReadAsLimitedStringAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes)
        {
            throw new HttpRequestException(
                $"Response body advertised {declared} bytes, which exceeds the controls fetcher cap of {MaxResponseBytes} bytes.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new MemoryStream();

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new HttpRequestException(
                    $"Response body exceeded the controls fetcher cap of {MaxResponseBytes} bytes.");
            }
            limited.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(limited.ToArray());
    }
}

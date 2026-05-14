// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Loads and caches the WinUI Gallery + Community Toolkit + core platform pattern dataset and
/// exposes a configured <see cref="SearchEngine"/> for the controls commands.
///
/// On first use (or when the on-disk cache is missing, expired, or schema-version mismatched) the
/// underlying fetchers will reach out to GitHub. Subsequent calls within the cache TTL are local-only.
/// Embedded JSON snapshots ship with the binary as a third-tier offline fallback.
/// </summary>
internal interface IControlsDataService
{
    /// <summary>
    /// Returns a configured <see cref="SearchEngine"/>. The engine is built lazily on first call
    /// and then memoized for the lifetime of the service instance.
    /// </summary>
    SearchEngine GetEngine();

    /// <summary>
    /// Deletes both the WinUI Gallery and Toolkit cache directories so the next <see cref="GetEngine"/>
    /// call re-fetches from GitHub.
    /// </summary>
    void ClearCache();
}

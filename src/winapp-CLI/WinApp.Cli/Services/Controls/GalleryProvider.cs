// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// WinUI 3 Gallery scenarios (<c>microsoft/WinUI-Gallery</c>). Parse logic lives
/// in <see cref="GalleryFetcher"/>; this type just wires it into the provider
/// model. Gallery contributes no author keywords.
/// </summary>
internal sealed class GalleryProvider : CachedProviderBase
{
    public GalleryProvider(string cacheRoot) : base(cacheRoot) { }

    public override string Id => "gallery";
    public override string DisplayName => "Gallery (WinUI 3)";

    protected override async Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
    {
        var (scenarios, tags) = await GalleryFetcher.FetchAsync(cancellationToken);
        return scenarios.Length > 0
            ? new ProviderData(scenarios, tags, new())
            : ProviderData.Empty;
    }
}

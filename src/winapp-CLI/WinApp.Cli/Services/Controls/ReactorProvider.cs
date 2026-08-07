// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// microsoft-ui-reactor ReactorGallery scenarios (C#-only declarative WinUI).
/// Parse logic lives in <see cref="ReactorFetcher"/>; this type just wires it into
/// the provider model. Reactor's curated per-control keywords are supplied as the
/// enrichment <see cref="ProviderData.Tags"/> field (3.0 weight) and served
/// verbatim — no stop-word cleaning (so multi-word intent terms like "css layout"
/// survive), hence no <c>NormalizeTagsOnRead</c> override. Reactor contributes no
/// separate curated-keyword field.
/// </summary>
internal sealed class ReactorProvider : CachedProviderBase
{
    public ReactorProvider(string cacheRoot) : base(cacheRoot) { }

    public override string Id => "reactor";
    public override string DisplayName => "Reactor (WinUI)";

    protected override async Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
    {
        var (scenarios, tags) = await ReactorFetcher.FetchAsync(cancellationToken).ConfigureAwait(false);
        return scenarios.Length > 0
            ? new ProviderData(scenarios, tags, new())
            : ProviderData.Empty;
    }
}

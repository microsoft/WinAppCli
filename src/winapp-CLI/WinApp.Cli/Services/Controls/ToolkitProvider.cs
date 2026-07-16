// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Windows Community Toolkit scenarios (<c>CommunityToolkit/Windows</c>). Parse
/// logic lives in <see cref="ToolkitFetcher"/>. Toolkit cleans its tag
/// dictionary on write, so it re-cleans on cache read too.
/// </summary>
internal sealed class ToolkitProvider : CachedProviderBase
{
    public ToolkitProvider(string cacheRoot) : base(cacheRoot) { }

    public override string Id => "toolkit";
    public override string DisplayName => "CommunityToolkit";

    protected override Dictionary<string, string[]> NormalizeTagsOnRead(
        Dictionary<string, string[]> tags) => StopWords.CleanTagDictionary(tags);

    protected override async Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
    {
        var (scenarios, tags, keywords) = await ToolkitFetcher.FetchAsync(cancellationToken);
        return scenarios.Length > 0
            ? new ProviderData(scenarios, tags, keywords)
            : ProviderData.Empty;
    }
}

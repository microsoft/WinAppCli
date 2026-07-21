namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Single source of truth for the on-disk cache version. Each provider's cache
/// lives under the managed global <c>.winapp/cache/find-ui/{providerId}</c>
/// directory (gallery / toolkit / reactor). <see cref="CachedProviderBase"/>
/// stamps this string into that provider's <c>schema-version.txt</c> on write
/// and requires an exact match on read; any mismatch forces a cache miss.
/// Unlike the upstream winui-search tool, find-ui ships NO embedded scenario
/// snapshot — a cache miss re-fetches the corpus from GitHub (network required).
///
/// Bump <see cref="Current"/> whenever ANY cached payload should be discarded:
///   1. Scenario / tag JSON schema changes (new or removed fields)
///   2. Embedded <c>Data/*.json</c> content changes (e.g. new tags added,
///      tag-list contents widened) — bump even if the C# schema is unchanged,
///      otherwise existing caches keep serving the older fallback contents.
///   3. Tag extraction / cleaning logic changes that would alter the cached
///      output for the same input data.
///
/// History:
///   "10" — Notes / Synonyms refactor
///   "11" — Added chip/token/tag entries to tokenizingtextbox in toolkit-tags.json
///   "12" — Added StopWords.TagOnly (text/input/layout/pick/basics/advanced)
///          → tag dicts cleansed; query tokens unchanged.
///   "13" — Toolkit cache now written CLEAN (CleanTagDictionary applied
///          before serialize), matching GalleryFetcher behavior. Old caches
///          contained polluted toolkit tags that were only filtered on read.
///   "14" — Plan A: separate keywords.json cache file; Plan B: HeaderText
///          is now the Sample's Header attribute alone (no " — Description"
///          suffix), Description holds the .md paragraph or XAML Description
///          attribute as a fallback.
///   "15" — Toolkit CleanCSharp now folds platform #if/#else/#endif (keeps
///          WINAPPSDK branch, drops UWP/Uno fallbacks) so emitted samples
///          compile clean against WinAppSDK without the noisy preprocessor.
///   "16" — Toolkit scenario IDs now renumbered in stable sample-path order
///          (was: alphabetical-by-slug, which reshuffled when upstream
///          rewords a Header). Old caches still resolve correctly inside a
///          single process but {controlId}-{N} differs across versions.
///   "17" — WinUI-Gallery moved + reformatted its samples: ControlInfoData.json
///          relocated to SampleSupport/Data/, pages are per-control under
///          Samples/{UniqueId}/, and ControlExample code now lives in
///          "--- header/xaml/c#" SampleDefinition .txt bundles. GalleryFetcher
///          parser rewritten and the embedded Data/gallery-*.json snapshot
///          regenerated from the new format — bump to discard old-format caches.
///   "18" — Legacy inline a11y samples with no leading comment now fall back to
///          the control Subtitle for HeaderText (was empty → "{Control}: "),
///          so the embedded gallery snapshot changed.
/// </summary>
internal static class CacheVersion
{
    public const string Current = "18";
}

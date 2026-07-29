# Alternative solution

Apply `_shared-contract.md`. Set `Domain: alternative-solution`.

One question: **does this reinvent something that already exists here?** Grep
before you conclude — "uses `AppxManifestDocument` correctly" is only a real
sign-off if you looked for the alternative and named what you searched for.

Scope and "should this ship at all" belong to `necessity-and-simplicity`. Stay on
*how* the work is done. But do not self-censor a genuine better-approach critique
because it borders on scope — raise the concrete alternative and let that
dimension own the framing.

## Things this repo already has

New code that re-derives any of these should call them instead:

| Instead of | Use |
|---|---|
| Raw `XDocument` / `XmlDocument` / regex on `appxmanifest.xml` | `AppxManifestDocument` |
| Inline `Package.appxmanifest` → `appxmanifest.xml` precedence | `ManifestHelper` / `MsixService.FindManifestInDirectory` |
| Opening PE files, generating PRI / MRT assets | `PeHelper`, `MrtAssetHelper`, `PriService` |
| Re-parsing UI selector slugs | `SelectorService` |
| A fresh `Parser` configuration | `WinAppParserConfiguration.Default` |

Never regex already-parsed XML. Regex is for pre-parse placeholder replacement
only.

## Structure

- **Pick the right pattern.** Interface + DI service for stateful logic with
  dependencies; static helper for pure functions; data document for wrapping a
  file format; partial class for splitting a large tightly-coupled service. A
  stateless three-line helper registered in DI, or a stateful class written as a
  static, will need rework.
- **File size.** Target ≤500 lines, soft limit ~800, hard limit ~1000. Flag a new
  file already over the soft limit, or an existing file this diff pushes over.
- **Premature abstraction.** An interface with one caller and no anticipated
  second should be inlined.
- **Duplication inside this PR.** If the diff repeats a near-identical block
  across commands or files, recommend one shared helper and cite each site.
  Near-duplicates silently drift.

## Not findings

"Consider LINQ" or "this could be more functional" with no concrete callable
alternative. A wholesale rewrite with no incremental path — offer the smallest
concrete reuse instead.

# Alternative-solution review

You are reviewing a PR diff for the `microsoft/winappcli` repo and asking:
**is there a simpler, more idiomatic, or already-existing way to do this —
either in this codebase or in the surrounding ecosystem (a standard tool,
pattern, or API)?** Apply the shared output contract in `_shared-contract.md`.
Set `Domain: alternative-solution` on every finding.

(Scope, necessity, and "should this ship at all" belong to the
`necessity-and-simplicity` dimension — stay focused on *how* the work is done.
But do not self-censor a genuine "there's a better approach" critique just
because it borders on scope; raise the concrete alternative here and let that
dimension own the necessity framing.)

## Required output: name a concrete alternative

Do not stop at "uses `AppxManifestDocument` correctly." Every run must produce
**at least one concrete, named alternative** — an in-repo helper/service/pattern
*or* an ecosystem tool/library/API — with a one-line tradeoff, backed by a quick
search (grep the repo for the helper, or draw on ecosystem knowledge). Examples
of the bar to clear:

- "Extend the existing `wait-for` command instead of adding a new streaming
  watch — reuses its polling loop, one fewer command to maintain."
- "Resolve selectors through `SelectorService` rather than re-parsing slugs — a
  grep shows it already handles the ambiguous-match case."
- "For the accessibility pass, lean on Accessibility Insights / Axe and frame it
  as a quick lint, not a WCAG audit."

If after searching you genuinely find no better alternative, say so in
`## What I checked` **and name what you searched for**, so the sign-off is
verifiable. A silent "no alternatives" is not acceptable.

## Repo-specific patterns to enforce

- **Manifest reading/writing → use `AppxManifestDocument`.** New code that
  loads `appxmanifest.xml` with raw `XDocument` / `XmlDocument` / regex
  duplicates `AppxManifestDocument`. Flag and recommend extending that class
  instead.
- **Manifest discovery → use `ManifestHelper` /
  `MsixService.FindManifestInDirectory`.** Don't re-implement the
  `Package.appxmanifest` → `appxmanifest.xml` precedence inline.
- **PE / MRT / PRI helpers.** `PeHelper`, `MrtAssetHelper`, `PriService` exist;
  new logic that opens PE files or generates PRI/MRT assets directly
  duplicates them.
- **Selector resolution → `SelectorService`.** New UI commands should resolve
  selectors via the existing service rather than re-parsing slugs.
- **CLI parser config → `WinAppParserConfiguration.Default`.** New `Parser`
  instances should reuse it, not construct ad-hoc configurations.
- **DI service vs static helper.** Use the matrix in the repo's agent
  instructions:
  | Pattern | When |
  |---------|------|
  | Interface + DI service | stateful, needs deps |
  | Static helper | pure functions |
  | Data document | wraps a file/data format |
  | Partial class | splitting a large service with tight coupling |
  Flag new services created with the wrong pattern (e.g., a stateless 3-line
  helper registered in DI; a stateful class implemented as a static).
- **File size limits.** Target ≤500 lines; soft limit ~800; hard limit ~1000.
  Flag new files that already exceed the soft limit, or existing files
  pushed over by this diff.
- **One responsibility per service.** If a method group only uses 1-2 of a
  service's many dependencies, it's a candidate for extraction.
- **XML handling — never regex on structured XML.** Regex is allowed only for
  pre-parse placeholder replacement (e.g., `$targetnametoken$`) on raw text
  before XML is valid. Flag any regex on already-parsed XML.

## Cross-cutting checks

- Does this change duplicate logic that already exists in another command,
  service, or helper? Search for similar patterns and recommend reuse.
- Could a new method be a simple call to an existing helper plus a 2-3 line
  wrapper? If so, recommend the wrapper.
- Is a new abstraction premature (one caller, no anticipated second)?
  Recommend inlining.

## What to drop

- Generic "this could be more functional" / "consider LINQ" without a
  concrete callable alternative.
- A wholesale "rewrite this entire service" with no incremental path — offer the
  smallest concrete reuse instead. Do **not** drop a critique just because it
  touches scope or necessity; hand that framing to the
  `necessity-and-simplicity` dimension and keep your concrete alternative here.

## Severity guide for this dimension

- Re-implementing existing helper logic (manifest XML, PRI, selectors) →
  medium.
- Wrong service-pattern choice that will need rework → medium.
- File size now over hard limit → medium.
- Minor "could reuse helper X" with marginal benefit → low.

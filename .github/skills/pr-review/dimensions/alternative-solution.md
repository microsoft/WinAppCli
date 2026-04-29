# Alternative-solution review

You are reviewing a PR diff for the `microsoft/winappcli` repo and asking:
**is there a simpler, more idiomatic, or already-existing way to do this in
this codebase?** Apply the shared output contract in `_shared-contract.md`.
Set `Domain: alternative-solution` on every finding.

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
  concrete callable alternative in the repo.
- Refactor suggestions that exceed the scope of the PR ("rewrite this whole
  service") — note them only as `low` with a tight recommendation, or skip.

## Severity guide for this dimension

- Re-implementing existing helper logic (manifest XML, PRI, selectors) →
  medium.
- Wrong service-pattern choice that will need rework → medium.
- File size now over hard limit → medium.
- Minor "could reuse helper X" with marginal benefit → low.
